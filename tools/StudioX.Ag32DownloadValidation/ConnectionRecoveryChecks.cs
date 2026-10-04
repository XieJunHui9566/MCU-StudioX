using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>离线重放下载失败分支；临时目录只有文本和空映像，不启动 OpenOCD。</summary>
internal static class ConnectionRecoveryChecks
{
    public static async Task<int> RunAsync(string sourceProject, string output)
    {
        var root = Path.GetFullPath(output);
        if (Directory.Exists(root))
        {
            throw new ArgumentException("Use a new validation directory.");
        }
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        var configuration = await new OpenOcdService(new ToolsetCatalog(root)).ConfigurationAsync(sourceProject)
            ?? throw new InvalidOperationException("An AG32 mapping project is required.");
        var target = Ag32PinMappingTargetScript.RequireCompatible(sourceProject, configuration.OpenOcd.TargetScript);
        Directory.CreateDirectory(Path.Combine(root, "device", "debug"));
        await File.WriteAllTextAsync(Path.Combine(root, "device", configuration.OpenOcd.TargetScript), target);
        Directory.CreateDirectory(Path.Combine(root, "scripts", "interface"));
        await File.WriteAllTextAsync(Path.Combine(root, "scripts", "interface", "cmsis-dap.cfg"), "# offline fixture\n");
        await File.WriteAllTextAsync(Path.Combine(root, "scripts", "interface", "jlink.cfg"), "# offline fixture\n");
        var toolset = new ResolvedToolset(new(1, "offline", "1", "win-x64", "none", new()
        {
            ["openocd"] = "openocd.exe",
            ["gdb"] = "gdb.exe"
        }, [],
            ResourceDirectories: new()
            {
                ["openocdScripts"] = "scripts"
            }), root, "offline");
        DownloadImageSnapshot[] images =
        [
            new(new("firmware.bin", "bin", new string('A', 64), 1672, 0x80000000, "application"), "firmware.bin", 1672),
            new(new("pin-mapping.bin", "bin", new string('B', 64), 99944, 0x80027000, "pin-mapping"), "pin-mapping.bin", 99944)
        ];
        var options = configuration.Options with
        {
            Serial = "fixed-dap-serial"
        };
        var arguments = OpenOcdService.CreatePinMappingArguments(root, configuration, options, toolset, images);
        var prepared = new DownloadPreparation(configuration, options, toolset, "firmware.bin", "firmware.bin", arguments, "fixture.log") { Images = images };
        var command = arguments[^1];
        Check(configuration.OpenOcd.Probes.Select(probe => probe.Id).SequenceEqual(["cmsis-dap", "jlink"]) &&
            configuration.OpenOcd.Probes[1].DisplayName.Contains("V9", StringComparison.Ordinal),
            "legacy project exposes DAP and J-Link V9 without changing the pack on disk");
        var legacy = configuration with
        {
            OpenOcd = configuration.OpenOcd with
            {
                Probes = [new("agm-blaster", "legacy", "interface/cmsis-dap.cfg", "swd", 1000)]
            },
            Options = options with
            {
                ProbeId = "agm-blaster"
            }
        };
        Check(OpenOcdDebugPlanner.ResolveProbe(legacy).Id == "cmsis-dap", "legacy saved probe aliases to DAP in debug plans");
        var legacyArguments = OpenOcdService.CreatePinMappingArguments(root, legacy, legacy.Options, toolset, images);
        Check(legacyArguments.SequenceEqual(arguments), "legacy DAP selection generates the same approved download command");
        var jlinkOptions = options with
        {
            ProbeId = "jlink"
        };
        var jlinkArguments = OpenOcdService.CreatePinMappingArguments(root, configuration, jlinkOptions, toolset, images);
        Check(jlinkArguments.Contains(Path.Combine(root, "scripts", "interface", "jlink.cfg")) &&
            jlinkArguments.Contains("transport select swd") && jlinkArguments[^1] == command,
            "J-Link changes only the SWD adapter and retains both image guards");
        var jlinkPlan = OpenOcdDebugPlanner.Create(root, configuration with
        {
            Options = jlinkOptions
        }, toolset, "firmware.elf");
        Check(jlinkPlan.OpenOcdArguments.Contains(Path.Combine(root, "scripts", "interface", "jlink.cfg")) &&
            jlinkPlan.InitializeCommands.Any(value => value.Contains("studiox_check_target", StringComparison.Ordinal)) &&
            jlinkPlan.OpenOcdArguments.Any(value => value.Contains("studiox_ag32_detach", StringComparison.Ordinal)) &&
            jlinkPlan.OpenOcdArguments.All(value => !value.Contains("cortex_m", StringComparison.Ordinal)),
            "J-Link debug preserves AG32 target checks and RISC-V detach behavior");
        foreach (var script in new[] { "debug/ag32vf303.cfg", "debug/ag32vf303cct6.cfg" })
        {
            var definition = configuration.OpenOcd with
            {
                TargetScript = script
            };
            var compatible = configuration with
            {
                Device = configuration.Device with
                {
                    OpenOcd = definition
                },
                OpenOcd = definition
            };
            var scriptFile = Path.Combine(root, "device", script);
            await File.WriteAllTextAsync(scriptFile, target);
            Check(DebugTargetProfile.Find(compatible.Device)?.IsAg32 == true, "verified target profile accepts " + script);
            foreach (var probeId in new[] { "cmsis-dap", "jlink", "agm-blaster" })
            {
                var selected = compatible with
                {
                    Options = options with
                    {
                        ProbeId = probeId
                    }
                };
                var mappingArguments = OpenOcdService.CreatePinMappingArguments(root, selected, selected.Options, toolset, images);
                Check(mappingArguments.Contains(target) && mappingArguments[^1] == command &&
                    OpenOcdDebugPlanner.ResolveProbe(selected).Transport == "swd",
                    script + " retains embedded guards and debug probe selection: " + probeId);
            }
            await File.WriteAllTextAsync(scriptFile, target + "\nproc studiox_check_target {} {}\n");
            Reject(() => OpenOcdService.CreatePinMappingArguments(root, compatible, options, toolset, images),
                "modified script is rejected at declared path: " + script);
            File.Delete(scriptFile);
            Reject(() => OpenOcdService.CreatePinMappingArguments(root, compatible, options, toolset, images),
                "missing declared script never falls back to another path: " + script);
            await File.WriteAllTextAsync(scriptFile, target);
        }
        foreach (var script in new[] { "debug/ag32vf303kcu6.cfg", "debug/custom.cfg", "../ag32vf303cct6.cfg" })
        {
            var definition = configuration.OpenOcd with
            {
                TargetScript = script
            };
            var invalid = configuration with
            {
                Device = configuration.Device with
                {
                    OpenOcd = definition
                },
                OpenOcd = definition
            };
            Check(DebugTargetProfile.Find(invalid.Device) is null, "unverified script never enables debug: " + script);
            Reject(() => OpenOcdService.CreatePinMappingArguments(root, invalid, options, toolset, images),
                "unverified script never enables mapping download: " + script);
        }
        var otherScript = configuration.OpenOcd.TargetScript == "debug/ag32vf303.cfg"
            ? "debug/ag32vf303cct6.cfg" : "debug/ag32vf303.cfg";
        Reject(() => OpenOcdService.CreatePinMappingArguments(root, configuration with
        {
            OpenOcd = configuration.OpenOcd with
            {
                TargetScript = otherScript
            }
        }, options, toolset, images), "download configuration cannot substitute another path for the pack declaration");
        Reject(() => OpenOcdService.CreatePinMappingArguments(root, configuration with
        {
            Device = configuration.Device with
            {
                Id = "AG32VF303KCU6"
            }
        }, options, toolset, images), "compatible script names do not enable an unverified chip model");
        Reject(() => OpenOcdService.CreatePinMappingArguments(root, configuration with
        {
            Device = configuration.Device with
            {
                FlashBytes = 0x100000
            }
        }, options, toolset, images), "compatible script names do not relax physical Flash capacity checks");
        Reject(() => OpenOcdService.CreatePinMappingArguments(root, configuration with
        {
            Device = configuration.Device with
            {
                ToolsetVersion = "unverified"
            }
        }, options, toolset, images), "wrong vendor toolset still rejects mapping download");
        Reject(() => OpenOcdService.CreatePinMappingArguments(root, configuration with
        {
            OpenOcd = configuration.OpenOcd with
            {
                Probes = [new("jlink", "bad", "interface/jlink.cfg", "jtag", 1000)]
            }
        }, jlinkOptions, toolset, images), "AG32 JTAG does not silently substitute for verified SWD");
        Check(command.IndexOf(OpenOcdDownloadDiagnostics.ConnectionBegin, StringComparison.Ordinal) < command.IndexOf("init;", StringComparison.Ordinal) &&
            command.IndexOf(OpenOcdDownloadDiagnostics.ConnectionReady, StringComparison.Ordinal) > command.IndexOf("init;", StringComparison.Ordinal) &&
            command.IndexOf(OpenOcdDownloadDiagnostics.ConnectionReady, StringComparison.Ordinal) < command.IndexOf("studiox_check_target", StringComparison.Ordinal) &&
            command.IndexOf(OpenOcdDownloadDiagnostics.FlashWriteBegin, StringComparison.Ordinal) < command.IndexOf("flash write_image", StringComparison.Ordinal),
            "connection and write markers surround actual Tcl operations");
        Check(arguments.Contains("adapter serial \"fixed-dap-serial\"") && arguments.Contains("adapter speed 1000"),
            "same approved serial and speed remain in generated arguments");
        var failed = new ProcessResult(1, OpenOcdDownloadDiagnostics.ConnectionBegin + "\n", "Error: Error connecting DP: cannot read IDR\n", false, false);
        Check(OpenOcdDownloadDiagnostics.CanRetryConnection(prepared, failed), "fixed-probe init IDR failure permits one caller-controlled reconnect");
        Check(OpenOcdDownloadDiagnostics.CanRetryConnection(prepared, failed with
        {
            StandardError = "Warn : could not read product string: Pipe error\nError: unable to find a matching CMSIS-DAP device\n"
        }),
            "fixed-probe enumeration failure can reconnect before init completes");
        Check(!OpenOcdDownloadDiagnostics.CanRetryConnection(prepared with
        {
            Options = options with
            {
                Serial = null
            }
        }, failed),
            "no serial means no automatic probe reselection");
        Check(!OpenOcdDownloadDiagnostics.CanRetryConnection(prepared, failed with
        {
            TimedOut = true
        }), "timeout never reconnects");
        Check(!OpenOcdDownloadDiagnostics.CanRetryConnection(prepared, failed with
        {
            OutputTruncated = true
        }), "truncated phase evidence never reconnects");
        Check(!OpenOcdDownloadDiagnostics.CanRetryConnection(prepared, failed with
        {
            StandardOutput = ""
        }), "missing phase marker never reconnects");
        Check(!OpenOcdDownloadDiagnostics.CanRetryConnection(prepared, failed with
        {
            StandardOutput = "echo " + OpenOcdDownloadDiagnostics.ConnectionBegin
        }),
            "command text is not a phase marker");
        foreach (var marker in new[] { OpenOcdDownloadDiagnostics.ConnectionReady, OpenOcdDownloadDiagnostics.FlashWriteBegin,
            "STUDIOX_VERIFY_APPLICATION_BEGIN", "STUDIOX_DOWNLOAD_VERIFIED", "auto erase enabled", "wrote 4096 bytes", "verified 1672 bytes" })
        {
            Check(!OpenOcdDownloadDiagnostics.CanRetryConnection(prepared, failed with
            {
                StandardOutput = failed.StandardOutput + marker + "\n"
            }),
                "reject reconnect after " + marker);
        }
        foreach (var diagnostic in new[] { "Error: unknown command", "Expected AG32 256 KiB physical Flash", "AG32 read protection enabled", "Assertion failed" })
        {
            Check(!OpenOcdDownloadDiagnostics.CanRetryConnection(prepared, failed with
            {
                StandardError = diagnostic
            }), "reject non-transient error: " + diagnostic);
        }
        Check(!OpenOcdDownloadDiagnostics.CanRetryConnection(prepared, failed with
        {
            ExitCode = 3
        }), "OpenOCD abort never reconnects");
        Check(!OpenOcdDownloadDiagnostics.CanRetryConnection(prepared with
        {
            Images = [images[0]]
        }, failed), "unmarked single-image workflow never reconnects");
        Check(!OpenOcdDownloadDiagnostics.CanRetryConnection(prepared with
        {
            Options = jlinkOptions
        }, failed), "DAP recovery policy does not retry J-Link");
        var report = new DownloadReport(false, failed.StandardError, "fixture.log", 1, false);
        Check(report.Summary.Contains("DAP 与目标通信失败", StringComparison.Ordinal), "exit=1 status explains DP communication failure");
        Check(OpenOcdDownloadDiagnostics.FailureSummary("could not read product string: Pipe error\nunable to find a matching CMSIS-DAP device").Contains("USB", StringComparison.Ordinal),
            "enumeration pipe error is identified as USB failure");
        Check((report with
        {
            Log = "could not read product string",
            FailureReason = "final attempt reason"
        }).Summary.Contains("final attempt reason", StringComparison.Ordinal),
            "retained earlier attempt does not override final failure reason");
        await File.WriteAllLinesAsync(Path.Combine(root, "result.txt"), checks.Prepend("PASS — offline connection recovery checks; no hardware accessed"));
        return 0;

        void Check(bool condition, string description)
        {
            if (!condition)
            {
                throw new InvalidOperationException(description);
            }
            checks.Add(description);
            Console.WriteLine("PASS " + description);
        }

        void Reject(Action action, string description)
        {
            try
            {
                action();
            }
            catch (StudioXException) { Check(true, description); return; }
            throw new InvalidOperationException("Expected rejection: " + description);
        }
    }
}
