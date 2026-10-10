using System.Collections.Concurrent;
using System.Security.Cryptography;
using StudioX.Application.StcDebugging;
using StudioX.Devices;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

internal static class HardwareSourceChecks
{
    public static async Task<int> RunAsync(string port, string output)
    {
        if (port != "COM14")
        {
            throw new InvalidOperationException("Only the previously authorized COM14 IAP15F2K61S2 board is in scope.");
        }
        const string expected = "2DE74703C8FD2BCD3E444F0136C84DFB1F44DC862FD95F95CA2E37ED538C2CB6";
        Directory.CreateDirectory(output);
        var bundle = await SourceChecks.CreateBundleAsync(output);
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            if (!value)
            {
                throw new InvalidOperationException(name);
            }
            checks.Add(name);
            Console.WriteLine("PASS " + name);
        }
        await using var hub = new DeviceHub();
        await using var session = new Mon51DebugSession(output);
        var logs = new ConcurrentQueue<string>();
        session.Output += logs.Enqueue;
        await session.ConnectAsync(hub, new SerialTransport(new(port, 115200)), output);
        async Task<string> ImageHash()
        {
            var data = new List<byte>();
            for (ushort start = 0; start < 216; start += 128)
            {
                data.AddRange(await session.ReadMemoryAsync(Mon51MemorySpace.Code, start, Math.Min(128, 216 - start)));
            }
            return Convert.ToHexString(SHA256.HashData(data.ToArray()));
        }
        async Task WaitStop()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (session.State == DebugState.Running)
            {
                await Task.Delay(25, timeout.Token);
            }
            if (session.State != DebugState.Stopped)
            {
                throw new InvalidOperationException(session.Reason);
            }
        }
        var initialHash = await ImageHash();
        if (initialHash != expected)
        {
            await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
            {
                Success = false,
                BlockedByFirmwareMismatch = true,
                ActualSha256 = initialHash,
                ExpectedSha256 = expected,
                Download = false,
                RegisterOrBreakpointWrite = false,
                LogPath = session.LogPath
            });
            throw new InvalidOperationException("Board no longer contains the approved 216-byte test image. No new image is downloaded or source breakpoint/PC/register write issued.");
        }
        await session.LoadSymbolsAsync(bundle);
        await session.ChangeWatchAsync("counter", false);
        await session.ChangeWatchAsync("total", false);
        await session.ExecuteAsync(DebugAction.Reset);
        await session.ConfigureSourceBreakpointAsync(new("source", "probe.c", 7));
        await session.ExecuteAsync(DebugAction.Continue);
        await WaitStop();
        Check(session.Pc == 0xa8 && session.Snapshot.Frames.Length == 2 && session.Snapshot.Frames[1].Function == "main", "source breakpoint / verified two-frame stack");
        Check(await session.EvaluateAsync("counter") == "0" && await session.EvaluateAsync("total") == "0", "source variables match first-entry RAM");
        await session.ExecuteAsync(DebugAction.StepInto);
        Check(session.Pc == 0xad && await session.EvaluateAsync("counter") == "1" && await session.EvaluateAsync("total") == "0", "source-line step executes increment before XDATA addition");
        await session.ExecuteAsync(DebugAction.StepOut);
        await WaitStop();
        Check(session.Pc == 0xd1 && session.Snapshot.Registers.Single(r => r.Name == "SP").Value == "0x07" && await session.EvaluateAsync("total") == "1", "step-out returns to caller with restored SP");
        await session.ChangeSourceBreakpointAsync("source", null);
        await session.SetPcAsync(0xce);
        await session.ExecuteAsync(DebugAction.StepOver);
        await WaitStop();
        Check(session.Pc == 0xd1 && await session.EvaluateAsync("counter") == "2" && await session.EvaluateAsync("total") == "3", "step-over executes complete call and stops at return");
        await session.ConfigureSourceBreakpointAsync(new("condition", "probe.c", 7, Condition: "counter >= 4"));
        await session.ExecuteAsync(DebugAction.Continue);
        await WaitStop();
        Check(session.Pc == 0xa8 && await session.EvaluateAsync("counter") == "4" && session.SourceBreakpoints.Single().HitCount == 3, "false condition resumes until matching counter");
        await session.ConfigureSourceBreakpointAsync(new("condition", "probe.c", 7, Condition: "counter >= 5", IgnoreCount: 1));
        await session.ExecuteAsync(DebugAction.Continue);
        await WaitStop();
        Check(await session.EvaluateAsync("counter") == "6" && session.SourceBreakpoints.Single().IgnoreRemaining == 0, "conditional ignore count");
        await session.ConfigureSourceBreakpointAsync(new("condition", "probe.c", 7, Temporary: true, LogMessage: "counter={counter}"));
        await session.ExecuteAsync(DebugAction.Continue);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            while (!logs.Any(s => s.Contains("counter=7")))
            {
                await Task.Delay(25, timeout.Token);
            }
        }
        await session.ExecuteAsync(DebugAction.Pause);
        Check(session.SourceBreakpoints.Count == 0, "temporary logpoint emits real variable and removes itself");
        await session.RunToCursorAsync("probe.c", 8);
        await WaitStop();
        Check(session.Pc == 0xad && session.Reason.Contains("光标"), "run to executable source line");
        var disassembly = await session.ReadDisassemblyAsync(0xa8, 28);
        Check(disassembly.Instructions.First().Instruction == "MOV A,0x30" && disassembly.Instructions.Any(i => i.Instruction == "RET"), "live CODE disassembly");
        // 编辑值逐项回读并恢复，使用当前测试固件的 RAM；不访问 UART、芯片配置或监控区。
        foreach (var register in new[] { "A", "B", "DPTR", "R5" })
        {
            var original = Convert.ToUInt32(session.Snapshot.Registers.Single(r => r.Name == register).Value[2..], 16);
            await session.SetRegisterAsync(register, original ^ 1);
            await session.SetRegisterAsync(register, original);
            Check(session.Snapshot.Registers.Single(r => r.Name == register).Value == "0x" + original.ToString(register == "DPTR" ? "X4" : "X2"), "edit and restore register " + register);
        }
        var scratch = await session.ReadMemoryAsync(Mon51MemorySpace.Xdata, 0x30, 1);
        await session.WriteMemoryAsync(Mon51MemorySpace.Xdata, 0x30, [(byte)(scratch[0] ^ 1)]);
        await session.WriteMemoryAsync(Mon51MemorySpace.Xdata, 0x30, scratch);
        Check((await session.ReadMemoryAsync(Mon51MemorySpace.Xdata, 0x30, 1)).SequenceEqual(scratch), "edit and restore user XDATA scratch byte");
        var counter = long.Parse(await session.EvaluateAsync("counter"));
        await session.SetVariableAsync("counter", counter ^ 1);
        await session.SetVariableAsync("counter", counter);
        Check(await session.EvaluateAsync("counter") == counter.ToString(), "edit and restore source integer variable");
        Check(await ImageHash() == expected, "all software breakpoint bytes and full approved image restored");
        var logPath = session.LogPath;
        await session.StopAsync();
        using (var reserve = await hub.ReserveAsync("serial:COM14"))
        {
        }
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            Success = true,
            Hardware = true,
            Target = "IAP15F2K61S2",
            Port = port,
            FirmwareSha256 = expected,
            Checks = checks,
            Download = false,
            ConfigurationWrites = false,
            UserProgramResumed = true,
            SerialReleased = true,
            LogPath = logPath
        });
        return 0;
    }
}
