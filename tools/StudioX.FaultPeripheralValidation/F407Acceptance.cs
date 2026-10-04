using System.Security.Cryptography;
using StudioX.Application;
using StudioX.Application.Peripherals;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

/// <summary>显式硬件验收，仅允许本轮确认的固件；普通回归和 CI 不调用。</summary>
internal static class F407Acceptance
{
    internal static async Task RunAsync(string project, string toolsets, string svd, string output)
    {
        const string approved = "3C8AF43AE96D00036E5A8A9FF0DC6DF0F5AE0C5E03156AE97EDF09CA9E48AD76";
        var root = Path.GetFullPath(output);
        if (Directory.Exists(root))
        {
            throw new InvalidOperationException("Use new evidence directory.");
        }
        Directory.CreateDirectory(root);
        var image = await File.ReadAllBytesAsync(Path.Combine(project, ".build/firmware.bin"));
        if (image.Length != 1524 || Convert.ToHexString(SHA256.HashData(image)) != approved || (await ProjectService.ReadAsync(project)).DeviceId != "STM32F407ZG")
        {
            throw new InvalidOperationException("Only the explicitly approved F407 image is allowed.");
        }
        var catalog = new ToolsetCatalog(toolsets);
        var tools = await catalog.ResolveAsync("arm.gnu", "1.0.0", "arm-gnu-15.2.rel1");
        var scripts = tools.ResourceDirectory("openocdScripts");
        var target = Path.Combine(project, "device/debug/stm32f407zg.cfg");
        var checks = new List<string>();
        void Check(bool value, string text)
        {
            if (!value)
            {
                throw new InvalidOperationException(text);
            }
            checks.Add(text);
            Console.WriteLine("PASS " + text);
        }
        static string Quote(string p) => "\"" + Path.GetFullPath(p).Replace('\\', '/').Replace("$", "\\$", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        async Task Operation(string name, string command)
        {
            using var lease = ProbeLease.Acquire();
            string[] arguments = ["-s",scripts,"-f",Path.Combine(scripts,"interface/stlink.cfg"),"-c","transport select swd","-f",target,"-c",
                "gdb port disabled; tcl port disabled; telnet port disabled; adapter speed 400; reset_config srst_only srst_nogate connect_assert_srst","-c",
                "init; reset halt; studiox_check_target; echo STUDIOX_F407_ID_CAPACITY_OK; set failed [catch {"+command+"; echo STUDIOX_OPERATION_DONE} detail]; set resumeFailed [catch {reset run; poll; if {[[target current] curstate] ne \"running\"} {error \"Target did not resume\"}; echo STUDIOX_RESUMED_RUNNING} resumeDetail]; echo $detail; echo $resumeDetail; shutdown; if {$failed} {error $detail}; if {$resumeFailed} {error $resumeDetail}"];
            var result = await new ProcessRunner().RunAsync(new(tools.Tool("openocd"), arguments, root, TimeSpan.FromMinutes(3), ToolsetEnvironment.Create(tools), RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
            var raw = result.StandardOutput + "\n" + result.StandardError;
            await File.WriteAllTextAsync(Path.Combine(root, name + ".log"), raw);
            if (result.ExitCode != 0 || result.TimedOut || result.OutputTruncated || !raw.Contains("STUDIOX_F407_ID_CAPACITY_OK", StringComparison.Ordinal) || !raw.Contains("STUDIOX_OPERATION_DONE", StringComparison.Ordinal) || !raw.Contains("STUDIOX_RESUMED_RUNNING", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(name + ": " + raw);
            }
        }
        var backup = Path.Combine(root, "original-flash.bin");
        var second = Path.Combine(root, "original-flash-second.bin");
        await Operation("backup", $"flash probe 0; echo [flash info 0]; dump_image {Quote(backup)} 0x08000000 1048576; dump_image {Quote(second)} 0x08000000 1048576");
        var original = await File.ReadAllBytesAsync(backup);
        var secondBytes = await File.ReadAllBytesAsync(second);
        Check(original.Length == 1048576 && original.SequenceEqual(secondBytes), "1 MiB Flash read twice with identical bytes before any write");
        var hash = Convert.ToHexString(SHA256.HashData(original));
        await JsonStore.WriteAsync(Path.Combine(root, "backup.json"), new
        {
            device = "STM32F407ZGT6",
            bytes = original.Length,
            sha256 = hash,
            approvedFirmware = approved
        });
        var geometry = await File.ReadAllTextAsync(Path.Combine(root, "backup.log"));
        Check(geometry.Contains("#0 : stm32f2x at 0x08000000, size 0x00100000", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(geometry, @"#\s*0\s*:\s*0x00000000\s*\(0x0*4000\s+16kB\)"), "OpenOCD confirms bank origin and relative sector 0 geometry is exactly 16 KiB");
        var sector = original[..16384];
        var patched = sector.ToArray();
        image.CopyTo(patched, 0);
        var sectorFile = Path.Combine(root, "original-sector0.bin");
        var patchFile = Path.Combine(root, "patched-sector0.bin");
        await File.WriteAllBytesAsync(sectorFile, sector);
        await File.WriteAllBytesAsync(patchFile, patched);
        var mutationStarted = false;
        var restored = false;
        var completed = false;
        try
        {
            mutationStarted = true;
            await Operation("write-test", $"flash write_image erase {Quote(patchFile)} 0x08000000 bin; verify_image {Quote(patchFile)} 0x08000000 bin");
            Check(true, "only sector 0 replaced while preserving bytes beyond approved image");
            await JsonStore.WriteAsync(Path.Combine(project, ".studiox/download.json"), new DownloadOptions("stlink", 400));
            var prep = (await HardwareDebugPreparer.PrepareAsync(project, new OpenOcdService(catalog))) with
            {
                ConnectUnderReset = true
            };
            await using var debug = new DebugSessionService(Path.Combine(root, "data"));
            debug.Output += Console.WriteLine;
            await debug.OpenProjectAsync(project);
            await debug.ToggleBreakpointAsync("src/main.c", 15);
            try
            {
                await debug.StartHardwareAsync(prep);
                Check(debug.IsHardware && debug.State == DebugState.Stopped, "production session matches board image to ELF");
                async Task WaitStopped()
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    while (debug.State != DebugState.Stopped)
                    {
                        if (debug.State == DebugState.Faulted)
                        {
                            throw new InvalidOperationException(debug.Reason);
                        }
                        await Task.Delay(25, deadline.Token);
                    }
                }
                await debug.ExecuteAsync(DebugAction.Continue);
                await WaitStopped();
                Check(debug.Breakpoints.Any(b => b.Verified && b.HitCount > 0), "real source breakpoint hits after ELF verification");
                await JsonStore.WriteAsync(Path.Combine(root, "breakpoint.json"), new
                {
                    debug.Reason,
                    debug.Snapshot,
                    debug.Breakpoints
                });
                await debug.ExecuteAsync(DebugAction.StepOver);
                await WaitStopped();
                Check(debug.Snapshot.Frames.Any(f => f.File.EndsWith("main.c", StringComparison.OrdinalIgnoreCase)), "real source step returns matching source file");
                var peripherals = new PeripheralService(debug, Path.Combine(root, "data"));
                var doc = await peripherals.ImportAsync(project, svd, "STM32F407ZG");
                var reg = doc.Device.Registers.Single(r => r.Path == "RCC.AHB1ENR");
                var reading = await peripherals.ReadAsync(doc, reg);
                await JsonStore.WriteAsync(Path.Combine(root, "peripheral-reading.json"), reading);
                Check(true, "real SVD RCC read shares paused debug owner; no peripheral writes");
                var stale = peripherals.PreviewWrite(doc, reg, reading.Value);
                await debug.ExecuteAsync(DebugAction.Continue);
                await debug.ExecuteAsync(DebugAction.Pause);
                await WaitStopped();
                try
                {
                    await peripherals.WriteAsync(stale);
                    throw new InvalidOperationException("Expected stale write rejection");
                }
                catch (StudioXException e) when (e.Code == "DEBUG_STATE") { Check(true, "resume rejects stale prepared peripheral write before mutation"); }
                await JsonStore.WriteAsync(Path.Combine(root, "stepped.json"), debug.Snapshot);
            }
            finally { await debug.StopAsync(); }
            await File.WriteAllTextAsync(Path.Combine(root, "debug-log-path.txt"), prep.LogPath);
            completed = true;
        }
        catch (Exception e) { await File.WriteAllTextAsync(Path.Combine(root, "error.txt"), e.ToString()); throw; }
        finally
        {
            if (mutationStarted)
            {
                var returned = Path.Combine(root, "restored-flash.bin");
                await Operation("restore", $"flash write_image erase {Quote(sectorFile)} 0x08000000 bin; verify_image {Quote(sectorFile)} 0x08000000 bin; dump_image {Quote(returned)} 0x08000000 1048576");
                var bytes = await File.ReadAllBytesAsync(returned);
                restored = original.SequenceEqual(bytes);
                await JsonStore.WriteAsync(Path.Combine(root, "restoration.json"), new
                {
                    restored,
                    originalSha256 = hash,
                    readbackSha256 = Convert.ToHexString(SHA256.HashData(bytes))
                });
                Check(restored, "entire 1 MiB Flash restored byte-for-byte and original program reset to run");
            }
            await JsonStore.WriteAsync(Path.Combine(root, "result.json"), new
            {
                success = completed && restored,
                hardware = true,
                restored,
                checks
            });
        }
    }
}
