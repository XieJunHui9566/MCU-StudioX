using StudioX.Application.Serial;
using StudioX.Extensions;
using StudioX.Extensions.Abstractions;

internal static class ProtocolChecks
{
    public static async Task<int> RunAsync(string fixture)
    {
        var checks = 0;
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var host = Path.Combine(root, "src/StudioX.PluginHost/bin", configuration, "net10.0/StudioX.PluginHost.exe");
        var sourcePath = Path.Combine(root, "examples/protocols/sensor-frame.js");
        var source = await File.ReadAllTextAsync(sourcePath);
        var now = DateTimeOffset.UtcNow;
        var request = Convert.FromHexString("010300000002C40B");
        var response = ModbusRtuDecoder.WithCrc([1, 3, 4, 0, 42, 0x12, 0x34]);
        Check(ModbusRtuDecoder.Crc("123456789"u8) == 0x4b37, "Known CRC check value");
        Check(ModbusRtuDecoder.WithCrc([1, 3, 0, 0, 0, 2]).SequenceEqual(request), "Known RTU vector / CRC byte order");
        for (var split = 1; split < response.Length; split++)
        {
            var decoder = new ModbusRtuDecoder();
            Check(decoder.Feed(new(now, false, response[..split])).Count == 0, "Partial frame emitted");
            var frames = decoder.Feed(new(now, false, response[split..]));
            Check(frames.Count == 1 && frames[0].Status == "CRC 正确" && frames[0].Fields.Any(f => f.Value.Contains("UInt16 4660")), "Split response / values");
        }
        var d = new ModbusRtuDecoder();
        var both = d.Feed(new(now, true, request.Concat(request).ToArray()));
        Check(both.Count == 2 && both.All(f => f.Direction == "TX"), "Concatenated requests");
        var lots = d.Feed(new(now, false, Enumerable.Repeat(response, 120).SelectMany(b => b).ToArray()));
        Check(lots.Count == 120 && lots.All(f => f.Status == "CRC 正确"), "Large concatenated receive truncates frames");
        var bad = response.ToArray(); bad[^1] ^= 1;
        Check(d.Feed(new(now, false, bad)).Count == 0, "Bad CRC should wait for idle / resync");
        Check(d.Flush().Single().Status.Contains("CRC 错误"), "Bad CRC not visible");
        var recovery = d.Feed(new(now, false, new byte[] { 0xff, 0xfe, 0, 4 }.Concat(response).ToArray()));
        Check(recovery.Count == 2 && recovery[^1].Status == "CRC 正确", "Noise resynchronization lost valid response");
        Check(d.Feed(new(now, false, ModbusRtuDecoder.WithCrc([1, 0x83, 2]))).Single().Status == "异常响应", "Exception reply");
        Check(d.Feed(new(now, true, ModbusRtuDecoder.WithCrc([0, 6, 0, 0, 0, 1]))).Single().Fields.Any(f => f.Value.Contains("广播")), "Broadcast address");
        Check(d.Feed(new(now, true, ModbusRtuDecoder.WithCrc([1, 3, 0, 0, 0, 0]))).Single().Status == "字段异常", "Invalid quantity accepted");
        var multiple = ModbusRtuDecoder.WithCrc([1, 16, 0, 16, 0, 2, 4, 0, 1, 0, 2]);
        Check(d.Feed(new(now, true, multiple)).Single().Fields.Any(f => f.Name == "寄存器 +1" && f.Value.EndsWith("/ 2")), "Write registers content");
        d.Feed(new(now, false, response[..3])); d.Feed(new(now, false, [], true));
        Check(d.Feed(new(now, false, response)).Single().Status == "CRC 正确", "Boundary joined sessions");
        var unknown = ModbusRtuDecoder.WithCrc([1, 0x41, 0xaa, 0x55]);
        Check(d.Feed(new(now, false, unknown)).Count == 0 && d.Flush().Single().Status == "CRC 正确", "Vendor function raw frame");
        var slave = new ModbusRtuDecoder(ModbusRole.PcSlave);
        Check(slave.Feed(new(now, false, request)).Single().Fields.Any(f => f.Value == "请求"), "Slave role reversed");
        var monitor = new ModbusRtuDecoder(ModbusRole.Monitor);
        Check(monitor.Feed(new(now, false, request.Concat(response).ToArray())).Count == 2, "Passive monitoring");
        foreach (var pdu in new byte[][] { [1, 1, 0, 0, 0, 8], [1, 2, 0, 0, 0, 8], [1, 4, 0, 0, 0, 1], [1, 5, 0, 0, 0xff, 0],
            [1, 15, 0, 0, 0, 8, 1, 0xa5], [1, 22, 0, 1, 0xff, 0x00, 0x00, 0xff], [1, 23, 0, 0, 0, 1, 0, 1, 0, 1, 2, 0, 42] })
            Check(new ModbusRtuDecoder().Feed(new(now, true, ModbusRtuDecoder.WithCrc(pdu))).Single().Status == "CRC 正确", "Function request 0x" + pdu[1].ToString("X2"));
        await using (var client = await SerialScriptClient.StartAsync(host, source))
        {
            var sample = Convert.FromHexString("AA55030109C4CF");
            Check((await client.DecodeAsync(sample[..4], "RX", now, false)).Consumed == 0, "Script partial frame consumed");
            var decoded = await client.DecodeAsync(sample.Concat(sample).ToArray(), "RX", now, false);
            Check(decoded.Consumed == 14 && decoded.Frames.Length == 2 && decoded.Frames[0].Fields!["温度"] == "25.00 °C", "Script packet / temperature");
            sample[^1] ^= 1;
            Check((await client.DecodeAsync(sample, "RX", now, false)).Frames.Single().Status == "error", "Script checksum error");
            await client.ResetAsync(default); checks++;
        }
        await Reject("function decode(b,c) { while(true) {} }", true, "Infinite loop not stopped");
        await Reject("while(true) {}", false, "Initialization loop not stopped");
        await Reject("function decode(b,c) {return {consumed:b.length,frames:[{offset:999,length:1,summary:'bad'}]};}", true, "Out of range script frame accepted");
        await Reject("function decode(b,c) {throw new Error('script failure');}", true, "Script exception swallowed");
        await using (var client = await SerialScriptClient.StartAsync(host, "function decode(b,c) { return {consumed:b.length, frames:[{offset:0,length:b.length,summary:typeof System + '/' + typeof require + '/' + typeof fetch}]}; }"))
            Check((await client.DecodeAsync([1], "RX", now, false)).Frames[0].Summary == "undefined/undefined/undefined", "Unexpected IO/CLR bindings");
        await using (var protocol = new SerialProtocolMonitor(host))
        {
            await protocol.ConfigureAsync(new(SerialProtocol.ModbusRtu));
            protocol.Observe(new(DateTimeOffset.UtcNow, true, request));
            protocol.Observe(new(DateTimeOffset.UtcNow, false, response));
            await WaitFor(() => protocol.Snapshot()!.Frames.Length == 2);
            Check(protocol.Snapshot()!.Frames[0].Direction == "TX", "Pipeline direction");
            protocol.Clear();
            protocol.Observe(new(DateTimeOffset.UtcNow, false, response[..3]));
            await Task.Delay(160);
            Check(protocol.Snapshot()!.Frames.Single().Status == "不完整帧", "Idle did not finalize partial frame");
            await protocol.ConfigureAsync(new(SerialProtocol.JavaScript, ScriptPath: sourcePath));
            protocol.Observe(new(DateTimeOffset.UtcNow, false, [0xaa, 0x55, 3]));
            protocol.Observe(new(DateTimeOffset.UtcNow, false, [1, 9, 0xc4, 0xcf]));
            await WaitFor(() => protocol.Snapshot()!.Frames.Length == 1);
            Check(protocol.Snapshot()!.Frames.Single().Summary.Contains("25.00"), "Script stream buffering");
            protocol.Observe(new(DateTimeOffset.UtcNow, false, Convert.FromHexString("AA55030109C4CF"), Offline: true));
            await WaitFor(() => protocol.Snapshot()!.Frames.Length == 2);
            Check(protocol.Snapshot()!.Frames[1].Source.Contains("离线"), "Offline source marker missing");
            await protocol.ExportAsync(Path.Combine(fixture, "protocol.jsonl"));
            Check((await File.ReadAllTextAsync(Path.Combine(fixture, "protocol.jsonl"))).Contains("AA 55 03 01 09 C4 CF"), "Protocol export lost bytes");
            var brokenPath = Path.Combine(fixture, "broken.js");
            await File.WriteAllTextAsync(brokenPath, "function decode(){while(true){}}");
            await protocol.ConfigureAsync(new(SerialProtocol.JavaScript, ScriptPath: brokenPath));
            protocol.Observe(new(DateTimeOffset.UtcNow, false, [1]));
            await WaitFor(() => protocol.Snapshot()!.Failed);
            Check(protocol.Snapshot()!.Message.Contains("脚本"), "Pipeline script failure invisible");
            await protocol.ConfigureAsync(new(SerialProtocol.ModbusRtu));
            protocol.Observe(new(DateTimeOffset.UtcNow, false, response));
            await WaitFor(() => protocol.Snapshot()!.Frames.Length == 1);
            Check(!protocol.Snapshot()!.Failed, "Decoder cannot recover after failed script");
            for (var i = 0; i < 12; i++)
            {
                protocol.Observe(new(DateTimeOffset.UtcNow, false, Enumerable.Repeat(response, 100).SelectMany(b => b).ToArray()));
                await Task.Delay(30);
            }
            await WaitFor(() => protocol.Snapshot()!.Discarded > 0);
            Check(protocol.Snapshot()!.Frames.Length == 1000, "Protocol history not bounded");
        }
        Console.WriteLine($"PASS: {checks} protocol-specific checks (including isolated JavaScript host).");
        return checks;
        async Task Reject(string script, bool decode, string label)
        {
            var rejected = false;
            try { await using var client = await SerialScriptClient.StartAsync(host, script); if (decode) await client.DecodeAsync([1], "RX", now, false); }
            catch (IOException) { rejected = true; }
            Check(rejected, label);
        }
    }
    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(5000);
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
