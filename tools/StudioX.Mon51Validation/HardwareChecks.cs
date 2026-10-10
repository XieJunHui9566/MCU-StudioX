using System.Security.Cryptography;
using StudioX.Application.StcDebugging;
using StudioX.Devices;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

internal static class HardwareChecks
{
    // 此入口只验证本会话已授权的板卡和 216 字节测试固件；没有下载或擦除路径。
    public static async Task<int> RunAsync(string port, string output)
    {
        if (port != "COM14")
        {
            throw new InvalidOperationException("Hardware acceptance is scoped to the authorized COM14 board.");
        }
        const string expected = "2DE74703C8FD2BCD3E444F0136C84DFB1F44DC862FD95F95CA2E37ED538C2CB6";
        Directory.CreateDirectory(output);
        await using var hub = new DeviceHub();
        await using var session = new Mon51DebugSession(output);
        await session.ConnectAsync(hub, new SerialTransport(new(port, 115200)), output);
        async Task<string> ImageHash()
        {
            var bytes = new List<byte>();
            for (ushort address = 0; address < 216; address += 64)
            {
                bytes.AddRange(await session.ReadMemoryAsync(Mon51MemorySpace.Code, address, Math.Min(64, 216 - address)));
            }
            return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
        }
        if (await ImageHash() != expected)
        {
            throw new InvalidOperationException("Current firmware differs from the approved test image. No breakpoint or PC write issued.");
        }
        await session.ExecuteAsync(DebugAction.Reset);
        await session.AddBreakpointAsync(0xa8);
        await session.ExecuteAsync(DebugAction.Continue);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            while (session.State == DebugState.Running)
            {
                await Task.Delay(25, timeout.Token);
            }
        }
        if (session.State != DebugState.Stopped || session.Pc != 0xa8)
        {
            throw new InvalidOperationException("Native C# breakpoint did not stop at A8: " + session.Reason);
        }
        var counter = (await session.ReadMemoryAsync(Mon51MemorySpace.DataSfr, 0x30, 1))[0];
        var totalBytes = await session.ReadMemoryAsync(Mon51MemorySpace.Xdata, 0x20, 2);
        var total = totalBytes[0] | totalBytes[1] << 8;
        if (counter != 0 || total != 0)
        {
            throw new InvalidOperationException($"Unexpected first-entry RAM: {counter}/{total}");
        }
        await session.ExecuteAsync(DebugAction.StepInto);
        if (session.Pc != 0xaa)
        {
            throw new InvalidOperationException("8051 native step expected PC AA");
        }
        await session.ExecuteAsync(DebugAction.Continue);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            while (session.State == DebugState.Running)
            {
                await Task.Delay(25, timeout.Token);
            }
        }
        if (session.State != DebugState.Stopped || session.Pc != 0xa8)
        {
            throw new InvalidOperationException("Second breakpoint did not stop at A8");
        }
        counter = (await session.ReadMemoryAsync(Mon51MemorySpace.DataSfr, 0x30, 1))[0];
        totalBytes = await session.ReadMemoryAsync(Mon51MemorySpace.Xdata, 0x20, 2);
        total = totalBytes[0] | totalBytes[1] << 8;
        if (counter != 1 || total != 1)
        {
            throw new InvalidOperationException($"Unexpected second-entry RAM: {counter}/{total}");
        }
        await session.RemoveBreakpointAsync(0xa8);
        await session.ExecuteAsync(DebugAction.Continue);
        await Task.Delay(250);
        await session.ExecuteAsync(DebugAction.Pause);
        var pausePc = session.Pc;
        if (await ImageHash() != expected)
        {
            throw new InvalidOperationException("Test image changed after breakpoint restoration");
        }
        var log = session.LogPath;
        await session.StopAsync();
        using (var released = await hub.ReserveAsync("serial:COM14"))
        {
        }
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            Success = true,
            Hardware = true,
            Target = "IAP15F2K61S2",
            Port = port,
            Backend = "native C# Mon51",
            FirmwareSha256 = expected,
            CounterAtBreakpoint = counter,
            TotalAtBreakpoint = total,
            BreakpointPc = "0x00A8",
            StepPc = "0x00AA",
            PausePc = $"0x{pausePc:X4}",
            FlashDownload = false,
            OriginalBreakpointByteRestored = true,
            FinalState = "User program resumed; serial port released",
            LogPath = log
        });
        Console.WriteLine("PASS native C# Mon51 hardware breakpoint / step / pause / memory / restore / resume");
        return 0;
    }
}
