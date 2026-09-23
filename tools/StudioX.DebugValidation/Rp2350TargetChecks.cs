using StudioX.Application;
using StudioX.Application.CodeIntelligence;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;
using StudioX.Packages;

internal static class Rp2350TargetChecks
{
    public static async Task<int> RunAsync(string archive, string runtime, string output)
    {
        var root = Path.GetFullPath(output);
        if (Directory.Exists(root)) throw new ArgumentException("Use a new validation directory.");
        var pack = await new PackRepository(Path.Combine(root, "packs")).ImportAsync(Path.GetFullPath(archive));
        var catalog = new ToolsetCatalog(Path.Combine(Path.GetFullPath(runtime), "toolsets"));
        var builds = new BuildService(catalog);
        var downloads = new OpenOcdService(catalog);
        var device = pack.Manifest.Devices.Single();
        Check(device.Templates.Select(t => t.Id).SequenceEqual(new[] { "minimal", "blink", "multicore" }), "Three C templates");
        Check(!Directory.EnumerateFiles(pack.RootDirectory, "*", SearchOption.AllDirectories).Any(p => p.Contains("micropython", StringComparison.OrdinalIgnoreCase)), "No MicroPython resources");
        foreach (var template in device.Templates)
        {
            var project = Path.Combine(root, "中文 工程", template.Id);
            await new ProjectService().CreateAsync(pack, device.Id, template.Id, "RP2350_" + template.Id, project);
            var build = await builds.BuildAsync(project);
            Check(build.Success, build.Log);
            var prepared = await HardwareDebugPreparer.PrepareAsync(project, downloads);
            var config = prepared.Configuration;
            var profile = OpenOcdDebugPlanner.ResolveTarget(config);
            Check(profile.IsRp2350 && profile.HasFpu && !profile.IsWch && !profile.IsAg32, "RP2350 profile");
            var plan = OpenOcdDebugPlanner.Create(project, config, prepared.Tools, prepared.Elf, 43334);
            Check(plan.OpenOcdArguments.Contains("gdb flash_program disable") &&
                plan.OpenOcdArguments.Contains("$_TARGETNAME configure -work-area-backup 1") &&
                plan.InitializeCommands.Any(c => c.Contains("studiox_rp2350_readonly", StringComparison.Ordinal)), "Preserve RAM during probe; readonly verify afterwards");
            Check(plan.InitializeCommands.All(c => !c.Contains("target-download", StringComparison.Ordinal)), "Attach never downloads");
            var download = await downloads.PrepareAsync(project, config.Options);
            var operation = download.Arguments.Last();
            Check(operation.IndexOf("studiox_check_target", StringComparison.Ordinal) < operation.IndexOf("flash write_image", StringComparison.Ordinal), "Check target before writing");
            var script = "if {$SWD_MULTIDROP != 1 || $_TARGETNAME ne \"rp2350.cm0\"} {error CONFIG}; " +
                "proc read_memory {args} {global fake_id; return [list $fake_id]}; " +
                "proc flash {command args} {global fake_size; if {$command eq \"list\"} {return [list [list base 0x10000000 size $fake_size]]}}; " +
                "set fake_id 0x20004927; set fake_size 0x400000; studiox_check_target; " +
                "set fake_id 0x10002927; if {![catch {studiox_check_target}]} {error ID_GUARD}; " +
                "set fake_id 0x20004927; set fake_size 0x200000; if {![catch {studiox_check_target}]} {error SIZE_GUARD}; " +
                "set fake_size 0x800000; if {![catch {studiox_check_target}]} {error SIZE_GUARD}; " +
                "studiox_rp2350_readonly; echo RP2350_OFFLINE_PASS; shutdown";
            var runner = new ProcessRunner();
            var environment = ToolsetEnvironment.Create(prepared.Tools);
            var dry = await runner.RunAsync(new(plan.OpenOcd, ["-c", "noinit", .. plan.OpenOcdArguments, "-c", script], project,
                TimeSpan.FromSeconds(20), environment, RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
            await File.WriteAllTextAsync(Path.Combine(project, "openocd-noinit.log"), dry.StandardOutput + dry.StandardError);
            Check(dry.Success && (dry.StandardOutput + dry.StandardError).Contains("RP2350_OFFLINE_PASS", StringComparison.Ordinal), dry.StandardOutput + dry.StandardError);
            foreach (var invalid in new[] { device with { Id = "RP2350B" }, device with { Architecture = "riscv" }, device with { ToolsetId = "riscv.xpack" },
                device with { FlashBytes = 0x200000 }, device with { RamBytes = 512 * 1024 }, device with { OpenOcd = null } })
                Check(DebugTargetProfile.Find(invalid) is null, "Reject mismatched board/tool/memory");
            Reject(config with { Options = new("stlink", 1000) }, "DEBUG_PROBE");
            Reject(config with { TargetScriptText = "unverified" }, "DEBUG_CONFIG");
            await using (var session = new DebugSessionService(Path.Combine(root, "user-data")))
            {
                await session.OpenProjectAsync(project);
                Check(session.Watches.SequenceEqual(new[] { "$pc" }), "No irrelevant example watches");
            }
            await JsonStore.WriteAsync(Path.Combine(project, "debug-plan.json"), plan);
            Console.WriteLine("PASS " + template.Id + ": real build, download/debug preparation, OpenOCD noinit ID/flash guards, C-only templates, portable Unicode path");
            if (template.Id == "minimal")
            {
                await builds.SaveSettingsAsync(project, new(Optimization: CompilerOptimization.O2, DebugInfo: CompilerDebugInfo.Full));
                var optimized = await builds.BuildAsync(project);
                Check(optimized.Success, optimized.Log);
                await HardwareDebugPreparer.PrepareAsync(project, downloads);
                await builds.SaveSettingsAsync(project, new());
                Check((await builds.BuildAsync(project)).Success, "Restore default build options");
                await IntelligenceAsync(runtime, root, project);
                Console.WriteLine("PASS optimization override / restore; Pico SDK completions and declaration navigation");
            }
            void Reject(DownloadConfiguration invalid, string code)
            {
                try { OpenOcdDebugPlanner.Create(project, invalid, prepared.Tools, prepared.Elf); }
                catch (StudioXException ex) when (ex.Code == code) { return; }
                throw new InvalidOperationException("Expected " + code);
            }
        }
        await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), "PASS: 3 Pico SDK C templates; real portable builds, compiler settings, target guards, download/debug plans and language service. No hardware access.\n");
        return 0;
    }

    private static async Task IntelligenceAsync(string runtime, string output, string project)
    {
        await using var service = new CodeIntelligenceService(Path.GetFullPath(runtime), Path.Combine(output, "language-cache"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await service.StartAsync(project, timeout.Token);
        var text = "#include \"pico/stdlib.h\"\nvoid check(void) { gpio_set_d\n}\n";
        var items = await service.CompleteAsync("src/main.c", text, text.IndexOf("gpio_set_d", StringComparison.Ordinal) + "gpio_set_d".Length, timeout.Token);
        Check(items.Any(i => i.InsertText.Contains("gpio_set_dir", StringComparison.Ordinal)), "GPIO completion");
        text = "#include \"pico/stdlib.h\"\nvoid check(void) { sleep_ms(10); }\n";
        var position = text.IndexOf("sleep_ms", StringComparison.Ordinal) + 2;
        Check((await service.NavigateAsync("src/main.c", text, position, true, timeout.Token)).Count > 0, "SDK declaration");
        Check(await service.HoverAsync("src/main.c", text, position, timeout.Token) is not null, "SDK hover");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
