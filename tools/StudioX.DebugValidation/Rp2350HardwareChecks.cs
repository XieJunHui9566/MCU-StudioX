using StudioX.Application;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>仅显式 --rp2350-hardware 执行；备份、使用生产服务验证、最后恢复原 Flash。</summary>
internal static class Rp2350HardwareChecks
{
    public static async Task<int> RunAsync(string archive, string runtime, string output, string serial)
    {
        var root = Path.GetFullPath(output);
        if (Directory.Exists(root)) throw new ArgumentException("Use a new hardware evidence directory.");
        var pack = await new PackRepository(Path.Combine(root, "packs")).ImportAsync(Path.GetFullPath(archive));
        var catalog = new ToolsetCatalog(Path.Combine(Path.GetFullPath(runtime), "toolsets"));
        var downloads = new OpenOcdService(catalog);
        var builds = new BuildService(catalog);
        var projects = new List<string>();
        var touchedBytes = 0;
        foreach (var template in new[] { "minimal", "multicore" })
        {
            var project = Path.Combine(root, template);
            await new ProjectService().CreateAsync(pack, Rp2350DebugTarget.DeviceId, template, "RP2350_" + template, project);
            var build = await builds.BuildAsync(project);
            Check(build.Success, build.Log);
            await downloads.SaveOptionsAsync(project, new("cmsis-dap", 1000, serial));
            touchedBytes = Math.Max(touchedBytes, ((int)new FileInfo(Path.Combine(project, ".build/firmware.bin")).Length + 4095) & ~4095);
            projects.Add(project);
        }
        var prepared = await HardwareDebugPreparer.PrepareAsync(projects[0], downloads);
        var scripts = prepared.Tools.ResourceDirectory("openocdScripts");
        string[] baseArgs = ["-s", scripts, "-f", Path.Combine(scripts, "interface/cmsis-dap.cfg"), "-c", "adapter serial " + Quote(serial),
            "-f", Path.Combine(projects[0], "device/debug/rp2350-pico2.cfg"), "-c", "adapter speed 1000", "-c", "gdb port disabled", "-c", "tcl port disabled", "-c", "telnet port disabled"];
        var backup = Path.Combine(root, "original-flash-4MiB.bin");
        await RunOcd("backup.log", "init", "reset halt", "studiox_check_target", "dump_image " + Quote(backup) + " 0x10000000 0x400000",
            "echo [verify_image " + Quote(backup) + " 0x10000000 bin]", "reset run", "shutdown");
        var original = await File.ReadAllBytesAsync(backup);
        Check(original.Length == 0x400000, "Complete Flash backup");
        var prefix = Path.Combine(root, "original-touched-sectors.bin");
        await File.WriteAllBytesAsync(prefix, original[..touchedBytes]);
        Console.WriteLine("PASS complete Flash backup and verification; touched range " + touchedBytes + " bytes");
        var attempted = false;
        try
        {
            foreach (var project in projects)
            {
                attempted = true;
                var report = await downloads.DownloadAsync(project, new("cmsis-dap", 1000, serial));
                Check(report.Success, report.Log);
                Console.WriteLine("PASS production download: " + Path.GetFileName(project));
                await Task.Delay(250);
                await using var session = new DebugSessionService(Path.Combine(root, "debug-data"));
                await session.OpenProjectAsync(project);
                await session.ChangeWatchAsync("app_counter", false);
                var dual = Path.GetFileName(project) == "multicore";
                if (dual) await session.ChangeWatchAsync("core1_result", false);
                var lines = await File.ReadAllLinesAsync(Path.Combine(project, "src/main.c"));
                var line = Array.FindIndex(lines, text => text.Contains(dual ? "sleep_ms(100)" : "++app_counter", StringComparison.Ordinal)) + 1;
                await session.ToggleBreakpointAsync("src/main.c", line);
                await session.StartHardwareAsync(await HardwareDebugPreparer.PrepareAsync(project, downloads));
                Check(session.State == DebugState.Stopped && session.Snapshot.Registers.Any(r => r.Name == "pc"), "Attach and registers");
                await session.ExecuteAsync(DebugAction.Reset);
                await session.ExecuteAsync(DebugAction.Continue);
                await WaitStopped(session);
                Check(session.Snapshot.Frames[0].File == "src/main.c", "Source breakpoint");
                await JsonStore.WriteAsync(Path.Combine(project, "breakpoint-snapshot.json"), session.Snapshot);
                if (dual)
                    Check(Watch(session, "core1_result") == Watch(session, "app_counter") * 3 + 1, "Core 1 queue computation");
                else
                {
                    var before = Watch(session, "app_counter");
                    await session.ExecuteAsync(DebugAction.StepOver);
                    await WaitStopped(session);
                    Check(Watch(session, "app_counter") == before + 1, "Source step increments counter");
                    await JsonStore.WriteAsync(Path.Combine(project, "step-snapshot.json"), session.Snapshot);
                }
                foreach (var point in session.Breakpoints.ToArray()) await session.ChangeBreakpointAsync(point.Id, false);
                await session.ExecuteAsync(DebugAction.Continue);
                await Task.Delay(350);
                await session.ExecuteAsync(DebugAction.Pause);
                await WaitStopped(session);
                var count = Watch(session, "app_counter");
                Check(count > 1, "Resume/pause made progress");
                await session.StopAsync();
                Check(session.State == DebugState.Disconnected && (await File.ReadAllTextAsync(session.SessionLogPath!)).Contains("STUDIOX_DETACHED_RUNNING", StringComparison.Ordinal), "Both cores resumed after detach");
                await Task.Delay(350);
                await session.StartHardwareAsync(await HardwareDebugPreparer.PrepareAsync(project, downloads));
                Check(Watch(session, "app_counter") > count, "Reattach preserved live RAM and program kept running");
                await session.ExecuteAsync(DebugAction.Continue);
                await session.StopAsync();
                Console.WriteLine("PASS production debug: " + Path.GetFileName(project) + ", source breakpoint, registers, run/pause, reset, detach and reattach");
            }
        }
        finally
        {
            if (attempted)
            {
                await RunOcd("restore.log", "init", "reset init", "studiox_check_target",
                    "echo [flash write_image erase " + Quote(prefix) + " 0x10000000 bin]",
                    "echo [verify_image " + Quote(backup) + " 0x10000000 bin]", "reset run", "shutdown");
                Console.WriteLine("PASS original Flash restored; entire 4 MiB verified");
            }
        }
        await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), "PASS: production BuildService / OpenOcdService / DebugSessionService with WCH CMSIS-DAP at 1000 kHz. Minimal and dual-core C firmware run, debug and reattach. Original 4 MiB Flash restored and verified. No OTP writes.\n");
        return 0;

        async Task RunOcd(string log, params string[] commands)
        {
            using var lease = ProbeLease.Acquire();
            var result = await new ProcessRunner().RunAsync(new(prepared.Tools.Tool("openocd"), [.. baseArgs, .. commands.SelectMany(c => new[] { "-c", c })], root,
                TimeSpan.FromMinutes(5), ToolsetEnvironment.Create(prepared.Tools), RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
            await File.WriteAllTextAsync(Path.Combine(root, log), result.StandardOutput + result.StandardError);
            Check(result.Success, result.StandardOutput + result.StandardError);
        }
    }
    private static int Watch(DebugSessionService session, string name) => int.Parse(session.Snapshot.Watches.Single(w => w.Name == name).Value);
    private static async Task WaitStopped(DebugSessionService session)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (session.State == DebugState.Running) await Task.Delay(20, deadline.Token);
        Check(session.State == DebugState.Stopped, session.Reason);
    }
    private static string Quote(string path) => "\"" + path.Replace('\\', '/').Replace("\"", "\\\"").Replace("$", "\\$").Replace("[", "\\[").Replace("]", "\\]") + "\"";
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
