using System.Buffers.Binary;

namespace StudioX.Ai8051Isp;

internal static class GuardChecks
{
    private sealed class FakeTransport(params byte[][] replies) : IHidReportTransport
    {
        public List<byte[]> Writes { get; } = [];
        private readonly Queue<byte[]> queue = new(replies);
        public Task Write(byte[] report, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Writes.Add(report);
            return Task.CompletedTask;
        }
        public Task<byte[]> Read(int timeoutMs, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (queue.Count == 0)
            {
                throw new TimeoutException("Injected receive timeout.");
            }
            return Task.FromResult(Reply(queue.Dequeue()));
        }
    }
    private static byte[] Reply(byte[] payload)
    {
        var r = new byte[65];
        r[1] = 0xa5;
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(2), (ushort)payload.Length);
        payload.CopyTo(r, 4);
        r[64] = unchecked((byte)-r.Take(64).Sum(x => x));
        return r;
    }
    public static async Task<object> Run()
    {
        int checks = 0;
        async Task Reject(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or TimeoutException or OperationCanceledException) { checks++; return; }
            throw new Exception("Expected rejection.");
        }
        byte[] identity = Convert.FromHexString("510104FF000000000000000000000071000000");
        byte[] status = Convert.FromHexString("50026295608030012FFFBF3E3F26EDF7FC74550078B41183BD32561D201C190100010004A0260510800E89C2F94D5F7DA7B1F8FE0000");
        var f = new FakeTransport();
        var p = new BridgeProtocol(f, _ => { });
        await Reject(() => p.WriteConfiguration(new(40015200, 128, 48), default));
        await Reject(() => p.Program([0], default));
        await Reject(() => p.BeginProgramming(null, default));
        await Reject(() => p.Calibrate(new(1, "AI8051U-34K64", 32, 40000000), default));
        if (f.Writes.Count != 0)
        {
            throw new Exception("Invalid state caused hardware writes.");
        }
        checks++;
        byte[] wrong = status.ToArray();
        wrong[21] ^= 1;
        f = new(identity, wrong);
        p = new(f, _ => { });
        await Reject(() => p.Detect(default));
        if (f.Writes.Any(x => x[2] == 0x43))
        {
            throw new Exception("Wrong target accepted programming commands.");
        }
        checks++;
        f = new(identity, status, [0xff]);
        p = new(f, _ => { });
        await p.Detect(default);
        await Reject(() => p.BeginProgramming(null, default));
        if (f.Writes.Any(x => x[2] == 0x43 && x[8] == 3))
        {
            throw new Exception("Bad handshake allowed erase.");
        }
        checks++;
        f = new(identity, status, [1], [5], [3, 0x78, 0xb4, 0, 0, 0, 0, 0], [2, 0]);
        p = new(f, _ => { });
        await p.Detect(default);
        await p.BeginProgramming(null, default);
        await Reject(() => p.Program(new byte[128], default));
        int writes = f.Writes.Count;
        await Reject(() => p.Run(default));
        await Reject(() => p.WriteConfiguration(new(40015200, 128, 48), default));
        await Reject(() => p.Program([0], default));
        if (f.Writes.Count != writes || f.Writes.Count(x => x[2] == 0x45) != 1)
        {
            throw new Exception("Failure continued or retried programming.");
        }
        checks++;
        f = new(identity, status, [1], [5]);
        p = new(f, _ => { });
        await p.Detect(default);
        await p.BeginProgramming(null, default);
        await Reject(() => p.Program([0], default));
        await Reject(() => p.Run(default));
        f = new(identity, status);
        p = new(f, _ => { });
        await p.Detect(default);
        using (var c = new CancellationTokenSource())
        {
            c.Cancel();
            writes = f.Writes.Count;
            await Reject(() => p.BeginProgramming(null, c.Token));
            if (f.Writes.Count != writes)
            {
                throw new Exception("Canceled write transmitted.");
            }
            checks++;
        }
        foreach (var bad in new[] { new DeviceConfiguration(1, "AI8051U-34K64", 8, 40000000), new(1, "AI8051U-34K64", 32, 48000001), new(2, "AI8051U-34K64", 32, 40000000), new(1, "STC8", 32, 40000000) })
        {
            await Reject(() => { bad.Validate(); return Task.CompletedTask; });
        }
        var info = new TargetInfo("AI8051U-34K64", "7.4.1U", "78B4", 40015200, Convert.ToHexString(status));
        DeviceConfiguration.CheckExisting(info);
        checks++;
        status[10] ^= 1;
        await Reject(() => { DeviceConfiguration.CheckExisting(info with { RawStatus = Convert.ToHexString(status) }); return Task.CompletedTask; });
        byte[] options = DeviceConfiguration.BuildOptions(40015200, 128, 48);
        if (Convert.ToHexString(options) != "0400005AA5FFFFFF00FFFF00FFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00FF02629560803001FFFFFF3E3FBFAFF7FCFE")
        {
            throw new Exception("Options differ from official successful capture.");
        }
        checks++;
        await Reject(() => { DeviceConfiguration.BuildOptions(0, 128, 48); return Task.CompletedTask; });
        await Reject(() => { DeviceConfiguration.BuildOptions(40015200, 128, 0xff); return Task.CompletedTask; });
        foreach (var invalid in new[] { (0u, (byte)1, 24000000), (24000000u, (byte)0, 24000000),
            (24000000u, (byte)9, 24000000), (25000000u, (byte)1, 24000000), (48000000u, (byte)2, 48000000) })
        {
            await Reject(() => { DeviceConfiguration.BuildOptions(invalid.Item1, 128, 32, invalid.Item2, invalid.Item3); return Task.CompletedTask; });
        }
        return new
        {
            passed = true,
            checks,
            hardwareOpened = false
        };
    }
}
