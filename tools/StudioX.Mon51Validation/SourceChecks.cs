using System.Collections.Concurrent;
using System.Security.Cryptography;
using StudioX.Application;
using StudioX.Application.StcDebugging;
using StudioX.Devices;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

internal static class SourceChecks
{
    public static async Task RunAsync(string root, List<string> checks)
    {
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new InvalidOperationException(name);
            }
            checks.Add(name);
        }
        async Task Reject(Func<Task> action, string code, string name)
        {
            try
            {
                await action();
                throw new InvalidOperationException("Accepted: " + name);
            }
            catch (StudioXException ex) when (ex.Code == code) { checks.Add(name); }
        }
        var project = Path.Combine(root, "source-project");
        Directory.CreateDirectory(Path.Combine(project, ".studiox"));
        await JsonStore.WriteAsync(Path.Combine(project, ".studiox/project.json"), new ProjectManifest(1, "SourceProbe", "stc.mon51", "1.0.0", "", "IAP15F2K61S2", "bare", "stc.sdcc", "1.0.0", "sdcc-4.5.0-15242"));
        var bundle = await CreateBundleAsync(project);
        var parsed = Mon51Symbols.Parse(bundle.SymbolsText, project);
        Check(parsed.FunctionAt(0xa8)?.Name == "checkpoint" && parsed.LineAt(0xa8)?.Line == 7 && parsed.Symbols.Single(s => s.Name == "total").Address == 0x20, "real SDCC 4.5 CDB scope/link/type/function/line records");
        Check(parsed.Bind(Path.Combine(project, "probe.c"), 6) is null, "non-executable source lines are not guessed");
        var nested = Path.Combine(project, "nested");
        Directory.CreateDirectory(nested);
        File.Move(Path.Combine(project, "probe.c"), Path.Combine(nested, "probe.c"));
        Check(Mon51Symbols.Parse(bundle.SymbolsText, project).Lines.All(l => l.File == Path.Combine(nested, "probe.c")), "CDB basename resolves unique nested project source");
        File.Copy(Path.Combine(nested, "probe.c"), Path.Combine(project, "probe.c"));
        await Reject(() => Task.FromResult(Mon51Symbols.Parse(bundle.SymbolsText, project)), "MON51_CDB", "ambiguous CDB basenames never guess a source file");
        Check(Mon51Symbols.Parse(bundle.SymbolsText, project, new[] { Path.Combine(nested, "probe.c") }).Lines.All(l => l.File == Path.Combine(nested, "probe.c")), "recorded compiler source resolves basename despite unused same-name templates");
        File.Delete(Path.Combine(nested, "probe.c"));
        Check(Mcs51Decoder.Decode(0xce, new byte[] { 0x12, 0, 0xa8 }).Target == 0xa8 && Mcs51Decoder.Decode(0xce, new byte[] { 0x12, 0, 0xa8 }).IsCall, "LCALL target and three-byte length");
        Check(Mcs51Decoder.Decode(0x17fe, new byte[] { 0x71, 0x35 }).Target == 0x1b35, "ACALL uses page of following instruction");
        Check(Mcs51Decoder.Decode(0xd1, new byte[] { 0x80, 0xfb }).Target == 0xce && Mcs51Decoder.Decode(0, new byte[] { 0x85, 0x30, 0x31 }).Text == "MOV 0x31,0x30", "signed relative branch and reversed MOV direct operands");
        Check(Mcs51Decoder.Length(0xaf) == 2 && Mcs51Decoder.Length(0x8f) == 2 && Mcs51Decoder.Length(0xbf) == 3 && Mcs51Decoder.Length(0xfe) == 1, "direct/register/CJNE instruction widths");
        for (var op = 0; op < 256; op++)
        {
            var instruction = Mcs51Decoder.Decode(0x400, new byte[] { (byte)op, 0x22, 0x33 });
            if (instruction.Length is < 1 or > 3 || instruction.Text.Length == 0)
            {
                throw new InvalidOperationException("Undecoded opcode " + op);
            }
        }
        checks.Add("all 256 opcode encodings bounded and complete");
        await using var hub = new DeviceHub();
        var fixture = new Mon51OfflineTransport { UseMemoryImage = true };
        bundle.Code.CopyTo(fixture.Code, 0);
        fixture.DataRam[8] = 0xd1;
        await using var debug = new DebugSessionService(Path.Combine(root, "source-user-data"), hub, _ => fixture);
        await debug.OpenProjectAsync(project);
        await debug.StartMon51Async("COM14", 115200, "IAP15F2K61S2");
        var monitor = debug.MonitorSession!;
        Check(debug.Watches.SequenceEqual(new[] { "$PC", "$A", "$SP" }), "connection preserves STC default register watches");
        fixture.Code[0xaa] ^= 1;
        await Reject(() => monitor.LoadSymbolsAsync(bundle), "MON51_IMAGE_MISMATCH", "one target byte mismatch rejects source activation");
        Check(!monitor.HasSymbols && monitor.State == DebugState.Stopped, "mismatch preserves paused address session");
        fixture.Code[0xaa] ^= 1;
        await monitor.LoadSymbolsAsync(bundle);
        await monitor.SetPcAsync(0xa8);
        Check(monitor.HasSymbols && debug.Snapshot.Frames.Length == 2 && debug.Snapshot.Frames[1].Function == "main", "SP/return bytes/LCALL prove caller frame");
        await debug.ChangeWatchAsync("counter", false);
        await debug.ChangeWatchAsync("total", false);
        await debug.ChangeWatchAsync("counter + total * 2", false);
        Check(debug.Snapshot.Watches.Single(w => w.Name == "counter").Value.StartsWith("0 ("), "DATA variable linked address");
        fixture.DataRam[0x30] = 2;
        fixture.XdataRam[0x20] = 0x34;
        fixture.XdataRam[0x21] = 0x12;
        await debug.RefreshAsync();
        Check(debug.Snapshot.Watches.Single(w => w.Name == "total").Value.StartsWith("4660 (") && debug.Snapshot.Watches.Single(w => w.Name == "counter").Changed, "little-endian 16-bit XDATA and change highlight");
        Check(await monitor.EvaluateAsync("counter + 1") == "3" && await monitor.EvaluateAsync("$PC") == "168", "read-only expressions and actual PC");
        fixture.StepReplies.Enqueue(0xaa);
        fixture.StepReplies.Enqueue(0xab);
        fixture.StepReplies.Enqueue(0xad);
        await debug.ExecuteAsync(DebugAction.StepInto);
        Check(monitor.Pc == 0xad, "source step traverses native instructions to next line");
        await monitor.SetPcAsync(0xce);
        fixture.RegisterImage[15] = 7;
        await debug.RefreshAsync();
        await debug.ExecuteAsync(DebugAction.StepOver);
        Check(debug.State == DebugState.Running && fixture.Code[0xd1] == 0xa5, "step-over runs to temporary call return");
        fixture.SignalBreakpoint(0xd1);
        await WaitStopAsync(debug);
        Check(monitor.Pc == 0xd1 && fixture.Code[0xd1] == 0x80 && monitor.Breakpoints.Count == 0, "step-over restores temporary opcode");
        await monitor.SetPcAsync(0xa8);
        fixture.RegisterImage[15] = 9;
        await debug.RefreshAsync();
        await debug.ExecuteAsync(DebugAction.StepOut);
        fixture.RegisterImage[15] = 7;
        fixture.SignalBreakpoint(0xd1);
        await WaitStopAsync(debug);
        Check(monitor.Pc == 0xd1 && monitor.Reason.Contains("跳出"), "step-out verifies caller return and SP");
        await monitor.SetPcAsync(0xa8);
        fixture.RegisterImage[15] = 9;
        await debug.RefreshAsync();
        await debug.ConfigureBreakpointAsync("probe.c", 7, new(Condition: "counter >= 3", IgnoreCount: 1));
        fixture.DataRam[0x30] = 2;
        fixture.StepReplies.Enqueue(0xaa);
        fixture.StepReplies.Enqueue(0xaa);
        await debug.ExecuteAsync(DebugAction.Continue);
        fixture.SignalBreakpoint(0xa8);
        await WaitRunningAtAsync(fixture, 0xaa);
        Check(debug.State == DebugState.Running, "false conditional breakpoint resumes after original opcode");
        fixture.DataRam[0x30] = 3;
        fixture.StepReplies.Enqueue(0xaa);
        fixture.SignalBreakpoint(0xa8);
        await WaitRunningAtAsync(fixture, 0xaa);
        fixture.SignalBreakpoint(0xa8);
        await WaitStopAsync(debug);
        Check(debug.Breakpoints.Single().HitCount == 3 && debug.Breakpoints.Single().IgnoreRemaining == 0, "conditional ignore-count and arrival count");
        var logged = new ConcurrentQueue<string>();
        debug.Output += logged.Enqueue;
        await debug.ConfigureBreakpointAsync("probe.c", 7, new(Temporary: true, LogMessage: "counter={counter}"));
        fixture.StepReplies.Enqueue(0xaa);
        await debug.ExecuteAsync(DebugAction.Continue);
        fixture.SignalBreakpoint(0xa8);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
        {
            while (!logged.Any(s => s.Contains("counter=3")))
            {
                await Task.Delay(25, timeout.Token);
            }
        }
        await debug.ExecuteAsync(DebugAction.Pause);
        Check(debug.Breakpoints.Count == 0 && fixture.Code[0xa8] == 0xe5, "temporary logpoint emits values and removes patch");
        await debug.RunToCursorAsync("probe.c", 8);
        fixture.SignalBreakpoint(0xad);
        await WaitStopAsync(debug);
        Check(monitor.Pc == 0xad && monitor.Reason.Contains("光标") && fixture.Code[0xad] == 0xaf, "run-to-cursor reports source stop and restores opcode");
        var disassembly = await debug.ReadDisassemblyAsync(0xa8, 28);
        Check(disassembly.Instructions[0].Instruction == "MOV A,0x30" && disassembly.Instructions[0].Function == "checkpoint" && disassembly.Instructions.Any(i => i.Instruction == "RET"), "live CODE disassembly with function names");
        await monitor.WriteMemoryAsync(Mon51MemorySpace.Xdata, 0x21, [0x56]);
        Check(fixture.XdataRam[0x21] == 0x56, "explicit XDATA edit and verified readback");
        await Reject(() => monitor.WriteMemoryAsync(Mon51MemorySpace.Code, 0xa8, [0]), "MON51_WRITE_RANGE", "general CODE edit rejected");
        await Reject(() => monitor.WriteMemoryAsync(Mon51MemorySpace.Idata, 0x81, [1]), "MON51_WRITE_RANGE", "unverified upper IDATA write rejected");
        await monitor.SetRegisterAsync("A", 0x33);
        await monitor.SetRegisterAsync("DPTR", 0x4567);
        Check(debug.Snapshot.Registers.Single(r => r.Name == "A").Value == "0x33" && debug.Snapshot.Registers.Single(r => r.Name == "DPTR").Value == "0x4567", "register context writes checked against saved registers");
        await monitor.SetVariableAsync("counter", 8);
        Check(fixture.DataRam[0x30] == 8, "fixed RAM integer variable edit verified");
        await Reject(() => monitor.SetVariableAsync("counter", 256), "MON51_VARIABLE", "variable integer width enforced");
        fixture.DataRam[8] = 0x33;
        await monitor.SetPcAsync(0xa8);
        fixture.RegisterImage[15] = 9;
        await debug.RefreshAsync();
        Check(debug.Snapshot.Frames.Length == 1, "arbitrary stack bytes never become guessed callers");
        await Reject(() => debug.ExecuteAsync(DebugAction.StepOut), "MON51_STACK", "unverified caller cannot start step-out");
        await File.AppendAllTextAsync(Path.Combine(project, "probe.c"), "\n/* changed */\n");
        await Reject(() => debug.ExecuteAsync(DebugAction.Continue), "MON51_SOURCE_CHANGED", "changed source blocks resume");
        Check(debug.State == DebugState.Stopped, "capability rejection preserves paused state");
        await debug.StopAsync();
        Check(fixture.Running && fixture.Code[0xa8] == 0xe5 && fixture.Code[0xd1] == 0x80, "source patches restored and program resumed on exit");
        await debug.OpenProjectAsync(project);
        Check(debug.Watches.Contains("counter + total * 2"), "integer watch preferences survive project reopen");
        await debug.ToggleBreakpointAsync("probe.c", 7);
        fixture = new Mon51OfflineTransport();
        await debug.StartMon51Async("COM14", 115200, "IAP15F2K61S2");
        Check(debug.Breakpoints.Count == 1 && debug.Watches.Contains("counter + total * 2") && !debug.Breakpoints[0].Verified, "connection preserves saved watches and unbound source breakpoint preferences");
        await debug.StopAsync();
    }

    public static async Task<StcDebugArtifact> CreateBundleAsync(string project)
    {
        Directory.CreateDirectory(project);
        foreach (var file in new[] { "probe.c", "probe.cdb", "probe.ihx" })
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "mon51-fixtures", file), Path.Combine(project, file), true);
        }
        var imagePath = Path.Combine(project, "probe.ihx");
        var cdbPath = Path.Combine(project, "probe.cdb");
        var image = await File.ReadAllBytesAsync(imagePath);
        var cdb = await File.ReadAllBytesAsync(cdbPath);
        var (code, present) = StcDebugArtifacts.ParseImage(image);
        return new(project, imagePath, cdbPath, System.Text.Encoding.UTF8.GetString(cdb), code, present, Convert.ToHexString(SHA256.HashData(image)), Convert.ToHexString(SHA256.HashData(cdb)), "recorded SDCC 4.5 --debug protocol fixture");
    }
    private static async Task WaitRunningAtAsync(Mon51OfflineTransport fixture, ushort pc)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!fixture.Running || fixture.Pc != pc)
        {
            await Task.Delay(25, timeout.Token);
        }
    }
    private static async Task WaitStopAsync(DebugSessionService debug)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (debug.State == DebugState.Running)
        {
            await Task.Delay(25, timeout.Token);
        }
        if (debug.State != DebugState.Stopped)
        {
            throw new InvalidOperationException(debug.Reason);
        }
    }
}
