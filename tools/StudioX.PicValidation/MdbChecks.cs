namespace StudioX.PicValidation;

using StudioX.Application;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Engine.Pic;
using StudioX.Foundation;

internal static class MdbChecks
{
    public static async Task RunAsync(string output, string runtime, string debugRuntime, string archive)
    {
        if (Directory.Exists(output))
        {
            throw new ArgumentException("Use new evidence directory.");
        }
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool value, string label)
        {
            if (!value)
            {
                throw new InvalidOperationException(label);
            }
            checks.Add(label);
            Console.WriteLine("PASS " + label);
        }
        await using var services = new WorkbenchService(runtime, Path.Combine(output, "data"));
        var pack = await services.Packs.ImportAsync(archive);
        var root = Path.Combine(output, "project");
        await services.Projects.CreateAsync(pack, "PIC16F877A", "xc8-basic", "PicDebug", root);
        var source = "#include <xc.h>\n#pragma config FOSC=XT, WDTE=OFF, PWRTE=OFF, BOREN=OFF, LVP=OFF, CPD=OFF, WRT=OFF, CP=OFF\nvolatile unsigned int counter;\nunsigned int add(unsigned int x)\n{\n    unsigned int value = x + 1;\n    value = value + 2;\n    return value;\n}\nvoid main(void)\n{\n    counter = 0;\n    for (;;)\n    {\n        counter = add(counter);\n        counter++;\n    }\n}\n";
        await File.WriteAllTextAsync(Path.Combine(root, "src/main.c"), source);
        var built = await services.Builds.BuildPicDebugAsync(root);
        await File.WriteAllTextAsync(Path.Combine(output, "build.log"), built.Log);
        Check(built.Success, "real CMake / Ninja / XC8 debug image built");
        var catalog = new ToolsetCatalog(Path.Combine(debugRuntime, "toolsets"));
        var engine = new PicDebugService(catalog, services.Builds.Pic, Path.Combine(output, "debug-data"));
        await services.Debugger.OpenProjectAsync(root);
        var logGate = new object();
        services.Debugger.Output += text => { lock (logGate) { File.AppendAllText(Path.Combine(output, "session.log"), text + "\n"); } };
        await services.Debugger.StartPicAsync(engine, null);
        Check(services.Debugger.State == DebugState.Stopped && !services.Debugger.IsHardware && services.Debugger.IsPicDebug,
            "real MDB simulator reaches main without connecting a probe");
        Check(services.Debugger.Snapshot.Frames.Any(f => f.File == "src/main.c"), "MDB source paths map to current project");
        try
        {
            await services.Builds.BuildAsync(root);
            throw new InvalidOperationException("Build during active debug accepted");
        }
        catch (StudioXException error) when (error.Code == "PROJECT_OUTPUT_BUSY") { Check(true, "active debug session prevents build output from being replaced"); }
        Check(services.Debugger.Snapshot.Registers.Any(r => r.Name == "WREG" && r.Value.StartsWith("0x", StringComparison.Ordinal)), "working register is read from the vendor backend");
        Check(PicProgrammerSelection.Automatic([new("ONE")])?.Serial == "ONE" && PicProgrammerSelection.Automatic([new("ONE"), new("TWO")]) is null,
            "one programmer is automatic, multiple programmers require selection");
        try
        {
            PicProgrammerSelection.Automatic([]);
            throw new InvalidOperationException("Missing programmer accepted");
        }
        catch (StudioXException error) when (error.Code == "PIC_PROGRAMMER_MISSING") { Check(true, "missing programmer fails without a selection dialog"); }
        await services.Debugger.ChangeWatchAsync("counter", false);
        Check(services.Debugger.Snapshot.Watches.Any(w => w.Name == "counter" && w.Value.StartsWith("0x", StringComparison.Ordinal)), "live symbol evaluation populates watches");
        await services.Debugger.ToggleBreakpointAsync("src/main.c", 16);
        Check(services.Debugger.Breakpoints.Single().Verified, "real MDB source breakpoint bound");
        await services.Debugger.ToggleBreakpointAsync("src/main.c", 7);
        Check(services.Debugger.Breakpoints.Count(p => p.Verified) == 1 && services.Debugger.Breakpoints.Any(p => p.Message?.Contains("只有 1 个", StringComparison.Ordinal) == true),
            "physical breakpoint limit is enforced even in simulator validation");
        await services.Debugger.ToggleBreakpointAsync("src/main.c", 7);
        await services.Debugger.ExecuteAsync(DebugAction.Continue);
        await WaitStopped();
        Check(services.Debugger.Snapshot.Frames.First().Line == 16, "continue stops at actual source breakpoint");
        Check((await services.Debugger.ReadMemoryAsync(0x20, 16)).Length == 32, "banked file register bytes are read from MDB");
        try
        {
            await services.Debugger.ReadMemoryAsync(uint.MaxValue, 16);
            throw new InvalidOperationException("Overflowing memory read accepted");
        }
        catch (ArgumentOutOfRangeException) { Check(true, "overflowing file-register address is rejected before sending a command"); }
        var original = Convert.FromHexString(await services.Debugger.ReadMemoryAsync(0x20, 1));
        await services.Debugger.WritePicFileRegistersAsync(0x20, [0x5a]);
        Check((await services.Debugger.ReadMemoryAsync(0x20, 1)).Equals("5a", StringComparison.OrdinalIgnoreCase), "file register writes are read back");
        await services.Debugger.WritePicFileRegistersAsync(0x20, original);
        var disassembly = await services.Debugger.ReadDisassemblyAsync(0, 8);
        Check(disassembly.Instructions.Length == 8 && disassembly.Instructions.All(i => i.Opcodes.Length == 4), "real PIC program-word disassembly and machine words are available");
        var originalPoint = services.Debugger.Breakpoints.Single().Id;
        await services.Debugger.RunToCursorAsync("src/main.c", 15);
        await WaitStopped();
        Check(services.Debugger.Snapshot.Frames.First().Line == 15 && services.Debugger.Breakpoints.Single().Id == originalPoint,
            "run to cursor borrows the single hardware slot and restores the original breakpoint");
        await services.Debugger.ChangeBreakpointAsync(services.Debugger.Breakpoints.Single().Id, null);
        await services.Debugger.ExecuteAsync(DebugAction.StepOver);
        await WaitStopped();
        Check(services.Debugger.State == DebugState.Stopped, "source next returns a stopped snapshot");
        await services.Debugger.ExecuteAsync(DebugAction.Continue);
        await Task.Delay(100);
        await services.Debugger.ExecuteAsync(DebugAction.Pause);
        await WaitStopped();
        Check(services.Debugger.State == DebugState.Stopped, "running program can be interrupted");
        await services.Debugger.ExecuteAsync(DebugAction.Reset);
        Check(services.Debugger.Snapshot.Registers.Single(r => r.Name == "pc").Value == "0x0", "reset refreshes PC without stale register cache");
        await services.Debugger.ToggleBreakpointAsync("src/main.c", 7);
        await services.Debugger.ExecuteAsync(DebugAction.Continue);
        await WaitStopped();
        Check(services.Debugger.Snapshot.Frames.Length > 1, "MDB supplies a real caller stack inside a function");
        Check(services.Debugger.Snapshot.Locals.Any(v => v.Name == "value"), "MDB full backtrace supplies local variables");
        await services.Debugger.RefreshAsync(1);
        Check(services.Debugger.Snapshot.SelectedFrame == 1 && services.Debugger.Snapshot.Watches.Any(w => w.Name == "counter" && w.Value.StartsWith("不可用", StringComparison.Ordinal)),
            "caller-frame watches never substitute a top-frame variable when MDB cannot select an evaluation frame");
        await services.Debugger.RefreshAsync(0);
        await services.Debugger.ChangeBreakpointAsync(services.Debugger.Breakpoints.Single().Id, null);
        await services.Debugger.ExecuteAsync(DebugAction.StepOut);
        await WaitStopped();
        Check(services.Debugger.Snapshot.Frames.First().Function == "main", "step out executes instructions until the real caller frame returns");
        await services.Debugger.ConfigureBreakpointAsync("src/main.c", 16, new(Condition: "counter > 5", IgnoreCount: 1, Temporary: true));
        await services.Debugger.ExecuteAsync(DebugAction.Continue);
        await WaitStopped();
        Check(services.Debugger.Breakpoints.Count == 0, "conditional, ignore-count and temporary breakpoint behavior reaches the real vendor simulator");
        await services.Debugger.StopAsync();
        Check(services.Debugger.State == DebugState.Disconnected, "session and vendor process are released");
        using (ProjectBuildStorage.Acquire(root, "pic"))
        {
            Check(true, "debug shutdown releases the output lease");
        }
        var tools = await catalog.ResolveAsync(PicDebugService.ToolsetId, PicDebugService.Version, PicDebugService.CompilerId, forceVerification: true);
        Check(true, "debug runtime content remains unchanged");
        await using (var process = new MdbProcess(tools, Path.Combine(output, "cancel-user"), Path.Combine(output, "cancel.log")))
        {
            try
            {
                await process.ExecuteAsync("echo valid\nquit");
                throw new InvalidOperationException("Multiple commands accepted");
            }
            catch (StudioXException error) when (error.Code == "MDB_COMMAND") { Check(true, "command injection is rejected"); }
            await process.ExecuteAsync("echo ready");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            try
            {
                await process.ExecuteAsync("sleep 5000", cancellation.Token);
                throw new InvalidOperationException("Canceled MDB command completed");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                Check(!process.IsAlive, "cancel kills the vendor process so late responses cannot enter another command");
            }
        }
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            passed = true,
            hardware = false,
            checks,
            root
        });

        async Task WaitStopped()
        {
            var timeout = DateTimeOffset.UtcNow.AddSeconds(20);
            while (services.Debugger.State == DebugState.Running && DateTimeOffset.UtcNow < timeout)
            {
                await Task.Delay(50);
            }
            if (services.Debugger.State != DebugState.Stopped)
            {
                throw new InvalidOperationException(services.Debugger.Reason);
            }
        }
    }
}
