using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;
using StudioX.Packages;

internal static class WchTargetChecks
{
    public static async Task<int> RunAsync(string archive, string runtime, string output)
    {
        var root = Path.GetFullPath(output);
        if (Directory.Exists(root)) throw new ArgumentException("Use a new validation directory.");
        var pack = await new PackRepository(Path.Combine(root, "packs")).ImportAsync(Path.GetFullPath(archive));
        var catalog = new ToolsetCatalog(Path.Combine(Path.GetFullPath(runtime), "toolsets"));
        var downloads = new OpenOcdService(catalog);
        var builds = new BuildService(catalog);
        foreach (var device in pack.Manifest.Devices)
        {
            var project = Path.Combine(root, device.Id);
            await new ProjectService().CreateAsync(pack, device.Id, "spl", device.Id, project);
            await using (var session = new StudioX.Application.DebugSessionService(Path.Combine(root, "user-data")))
            {
                await session.OpenProjectAsync(project);
                Check(session.Watches.SequenceEqual(new[] { "$pc" }), "New WCH projects do not inherit F407 example watches");
            }
            var build = await builds.BuildAsync(project);
            Check(build.Success, build.Log);
            var prepared = await HardwareDebugPreparer.PrepareAsync(project, downloads);
            var config = prepared.Configuration;
            var profile = OpenOcdDebugPlanner.ResolveTarget(config);
            Check(profile.IsWch && !profile.IsAg32 && profile.HasFpu == device.Id.StartsWith("CH32V307", StringComparison.Ordinal), "WCH core/FPU profile");
            if (device.Id.StartsWith("CH595", StringComparison.Ordinal))
                Check(profile.Core.Contains("V3C", StringComparison.Ordinal) && !profile.HasFpu, "CH595 QingKe V3C has no FPU");
            if (device.Id.StartsWith("CH592", StringComparison.Ordinal))
                Check(profile.Core.Contains("V4C", StringComparison.Ordinal) && !profile.HasFpu, "CH592 QingKe V4C has no FPU");
            var plan = OpenOcdDebugPlanner.Create(project, config, prepared.Tools, prepared.Elf, 43334);
            Check(plan.Gdb.EndsWith("riscv-wch-elf-gdb.exe", StringComparison.OrdinalIgnoreCase), "Vendor GDB");
            Check(plan.OpenOcdArguments.Contains("gdb_port 43334") && plan.OpenOcdArguments.Contains("bindto 127.0.0.1") &&
                plan.OpenOcdArguments.Contains("transport select sdi"), "Legacy ports, loopback and SDI");
            Check(plan.OpenOcdArguments.Contains("gdb_flash_program disable") &&
                plan.OpenOcdArguments.Contains("$_TARGETNAME.0 configure -work-area-size 0 -work-area-backup 1"), "Attach protects flash and live RAM");
            Check(plan.OpenOcdArguments.All(c => !c.Contains("cortex_m", StringComparison.Ordinal) && !c.Contains("write_image", StringComparison.Ordinal)) &&
                plan.InitializeCommands.All(c => !c.Contains("target-download", StringComparison.Ordinal)), "No ARM or flash programming commands");
            Check(plan.OpenOcdArguments.Any(c => c.Contains("STUDIOX_DETACHED_RUNNING", StringComparison.Ordinal)), "Confirmed resume on detach");
            var runner = new ProcessRunner();
            var environment = ToolsetEnvironment.Create(prepared.Tools);
            // noinit + shutdown：解析真实脚本和事件，但不初始化 USB 或连接芯片。
            var dry = await runner.RunAsync(new(plan.OpenOcd, ["-c", "noinit", .. plan.OpenOcdArguments,
                "-c", "echo STUDIOX_WCH_DEBUG_CONFIG_OK; shutdown"], project, TimeSpan.FromSeconds(15), environment,
                RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
            await File.WriteAllTextAsync(Path.Combine(project, "openocd-noinit.log"), dry.StandardOutput + dry.StandardError);
            Check(dry.Success && (dry.StandardOutput + dry.StandardError).Contains("STUDIOX_WCH_DEBUG_CONFIG_OK", StringComparison.Ordinal), dry.StandardOutput + dry.StandardError);
            var settings = plan.InitializeCommands.Where(c => c.StartsWith("-gdb-set ", StringComparison.Ordinal))
                .SelectMany(c => new[] { "-ex", "set " + c[9..] }).ToArray();
            var symbols = await runner.RunAsync(new(plan.Gdb, ["--nx", "--batch", .. settings, "-ex", "show architecture",
                "-ex", "info address main", prepared.Elf], project, TimeSpan.FromSeconds(15), environment,
                RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
            await File.WriteAllTextAsync(Path.Combine(project, "gdb-symbols.log"), symbols.StandardOutput + symbols.StandardError);
            Check(symbols.Success && symbols.StandardOutput.Contains("riscv:rv32", StringComparison.Ordinal) && symbols.StandardOutput.Contains("main", StringComparison.Ordinal), symbols.StandardOutput + symbols.StandardError);
            foreach (var invalid in new[] { device with { Id = "CH32V305VCT6" }, device with { Architecture = "arm" },
                device with { ToolsetId = "riscv.xpack" }, device with { CompilerId = "unknown" }, device with { FlashOrigin = 0x08000000 },
                device with { RamBytes = device.RamBytes + 1024 }, device with { FlashBytes = device.FlashBytes + 1024 }, device with { OpenOcd = null },
                device with { OpenOcd = device.OpenOcd! with { ApplicationFlashBytes = device.OpenOcd!.ApplicationFlashBytes!.Value - 1024 } } })
                Check(DebugTargetProfile.Find(invalid) is null, "Reject incorrect chip/tool/memory and old compile-only pack");
            Reject(config with { Options = new("cmsis-dap", 1000) }, "DEBUG_PROBE");
            Reject(config with { Options = new("wch-link", 1500) }, "DEBUG_OPTIONS");
            Reject(config with { Options = new("wch-link", 6000, "unsupported") }, "DEBUG_OPTIONS");
            Reject(config with { TargetScriptText = "unverified" }, "DEBUG_CONFIG");
            Reject(config with { OpenOcd = config.OpenOcd with { TargetScript = "debug/wrong.cfg" } }, "DEBUG_CONFIG");
            Reject(config with { OpenOcd = config.OpenOcd with { Probes = [config.OpenOcd.Probes[0] with { Transport = "swd" }] } }, "DEBUG_CONFIG");
            if (device.Id.StartsWith("CH32V203", StringComparison.Ordinal))
                await WchV203GuardChecks.RunAsync(pack.RootDirectory, project, device, await downloads.PrepareAsync(project, config.Options));
            if (device.Id.StartsWith("CH595", StringComparison.Ordinal))
                await WchCh595GuardChecks.RunAsync(project, device, await downloads.PrepareAsync(project, config.Options));
            if (device.Id.StartsWith("CH592", StringComparison.Ordinal))
                await WchCh592GuardChecks.RunAsync(project, device, await downloads.PrepareAsync(project, config.Options));
            await JsonStore.WriteAsync(Path.Combine(project, "debug-plan.json"), plan);
            Console.WriteLine("PASS " + device.Id + ": build, symbols, noinit configuration, attach/detach protections and invalid target/probe/settings; no hardware access");

            void Reject(DownloadConfiguration invalid, string code)
            {
                try { OpenOcdDebugPlanner.Create(project, invalid, prepared.Tools, prepared.Elf); }
                catch (StudioXException ex) when (ex.Code == code) { return; }
                throw new InvalidOperationException("Expected " + code);
            }
        }
        await RegisterChecks.RunRiscVAsync();
        await WchRegistersAsync();
        await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), $"PASS: {pack.Manifest.Id}, {pack.Manifest.Devices.Count} devices; offline debug plans, vendor GDB symbols, target/probe boundaries, RISC-V register mapping. No hardware access.\n");
        return 0;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static async Task WchRegistersAsync()
    {
        await using var adapter = new GdbDebugAdapter(new WchRegisterTransport(), new("CH32V307VCT6", "V4F", true));
        var snapshot = await adapter.ReadAsync([], 0, default);
        Check(snapshot.Registers.Select(r => r.Name).SequenceEqual(new[] { "pc", "mstatus", "zero", "ft0" }), "WCH sparse numbering and generic CSR exclusion");
        await using var noFpu = new GdbDebugAdapter(new WchRegisterTransport(), new("CH32V203C8T6", "V4B", false));
        var integerSnapshot = await noFpu.ReadAsync([], 0, default);
        Check(integerSnapshot.Registers.Select(r => r.Name).SequenceEqual(new[] { "pc", "mstatus", "zero" }), "V203 must not request nonexistent FPU registers");
        foreach (var name in new[] { "ft0", "fs0", "fa0", "f0", "f31", "fflags", "frm", "fcsr" })
            Check(!WchDebugTarget.IsVisibleRegister(name, false) && WchDebugTarget.IsVisibleRegister(name, true), "FPU capability: " + name);
        Check(WchDebugTarget.IsVisibleRegister("fp", false), "Integer frame pointer is not an FPU register");
    }
    private sealed class WchRegisterTransport : IGdbMiTransport
    {
        public event Action<string>? RecordReceived { add { } remove { } }
        public Task<string> ExecuteAsync(string command, CancellationToken token = default)
        {
            var at = command.IndexOf('-');
            var result = command[at..] switch
            {
                "-stack-list-frames 0 31" => "stack=[frame={level=\"0\",func=\"main\"}]",
                "-stack-select-frame 0" => "",
                "-stack-list-variables --simple-values" => "variables=[]",
                "-data-list-register-names" => "register-names=[\"\",\"mstatus\",\"vcsr\",\"pc\",\"zero\",\"ft0\",\"vsstatus\"]",
                "-data-list-register-values x 1 3 4 5" => "register-values=[{number=\"3\",value=\"0x226\"},{number=\"1\",value=\"0x1880\"},{number=\"4\",value=\"0\"},{number=\"5\",value=\"0x3f800000\"},{number=\"2\",value=\"0\"}]",
                "-data-list-register-values x 1 3 4" => "register-values=[{number=\"3\",value=\"0x226\"},{number=\"1\",value=\"0x1880\"},{number=\"4\",value=\"0\"},{number=\"5\",value=\"0\"}]",
                _ => throw new InvalidOperationException("Unexpected register request: " + command)
            };
            return Task.FromResult(command[..at] + "^done" + (result.Length == 0 ? "" : "," + result));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
