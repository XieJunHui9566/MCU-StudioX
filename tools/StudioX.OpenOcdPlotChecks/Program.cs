using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using StudioX.Application.OpenOcdPlot;
using StudioX.Engine.Debugging;

var checks = new List<string>();
void Check(bool value, string name)
{
    if (!value) { throw new InvalidOperationException(name); }
    checks.Add(name);
    Console.WriteLine("PASS " + name);
}
async Task Reject(Func<Task> action, string name)
{
    try { await action(); }
    catch (Exception) { Check(true, name); return; }
    throw new InvalidOperationException("Expected rejection: " + name);
}
async Task WaitUntil(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(5000);
    while (!condition()) { await Task.Delay(10, timeout.Token); }
}

Check(PlotScalarCodec.Decode([0xff], PlotScalar.Int8, true) == -1, "signed byte");
Check(PlotScalarCodec.Decode([0x80, 0], PlotScalar.Int16, false) == short.MinValue, "big endian signed 16-bit");
Check(PlotScalarCodec.Decode([0xff, 0xff, 0xff, 0xff], PlotScalar.UInt32, true) == uint.MaxValue, "unsigned 32-bit retains full range");
Check(PlotScalarCodec.Decode([0, 0, 0xc0, 0x3f], PlotScalar.Float32, true) == 1.5, "IEEE float decode");
Check(PlotScalarCodec.Decode([0x3f, 0xf0, 0, 0, 0, 0, 0, 0], PlotScalar.Float64, false) == 1, "IEEE double big endian");
Check(PlotScalarCodec.Infer("volatile const uint32_t") == PlotScalar.UInt32, "qualified C type");
await Reject(() => Task.FromResult(PlotScalarCodec.Infer("size_t")), "unknown typedef not guessed");
await Reject(() => Task.FromResult(PlotScalarCodec.Decode([1], PlotScalar.UInt32, true)), "short memory read rejected");
Check(OpenOcdMemoryClient.ParseResponse("OK 0x01 255 0 0x7f", 4).SequenceEqual(new byte[] { 1, 255, 0, 127 }), "Tcl decimal and hex bytes");
await Reject(() => Task.FromResult(OpenOcdMemoryClient.ParseResponse("ERR target not halted", 4)), "running-read refusal not treated as data");
await Reject(() => Task.FromResult(OpenOcdMemoryClient.ParseResponse("OK 1 2", 4)), "truncated RPC rejected");
await Reject(() => Task.FromResult(OpenOcdMemoryClient.ParseResponse("OK 256", 1)), "out of range byte rejected");

var mi = new SymbolTransport();
await using (var adapter = new GdbDebugAdapter(mi))
{
    var session = Guid.NewGuid();
    Task<OpenOcdPlotChannel> Resolve(string text, PlotScalar type = PlotScalar.Auto) =>
        OpenOcdPlotResolver.ResolveAsync(adapter, text, type, session, 0x20000000, 0x10000, true);
    var channel = await Resolve("counter");
    Check(channel.Address == 0x20000010 && channel.Type == PlotScalar.UInt32 && channel.SessionId == session, "ELF symbol resolved with session identity");
    Check(mi.Commands.Any(c => c.Contains("::counter")), "explicit global scope avoids local shadowing");
    Check(mi.LiveVariables == 0, "GDB temporary variable cleaned up");
    Check((await Resolve("samples[2]")).Address == 0x20000010, "fixed array element accepted");
    await Reject(() => Resolve("samples[4]"), "out of bounds array rejected");
    await Reject(() => Resolve("pointer[0]"), "pointer indexing rejected");
    await Reject(() => Resolve("counter=1"), "assignment rejected");
    await Reject(() => Resolve("function()"), "function call rejected");
    await Reject(() => Resolve("counter\nshutdown"), "command injection rejected");
    await Reject(() => Resolve("counter", PlotScalar.UInt16), "explicit size mismatch rejected");
    mi.Address = "0x40000000";
    await Reject(() => Resolve("counter"), "peripheral register polling rejected");
    mi.Address = "0x2000ffff";
    await Reject(() => Resolve("counter"), "RAM end overflow rejected");
    Check(mi.LiveVariables == 0, "failure paths clean up GDB variables");
}

await using (var listener = new TestServer(async stream =>
{
    var command = await TestServer.ReadFrame(stream);
    Check(command.Contains("0x20000010 4") && !command.Contains("halt") && !command.Contains("resume") && !command.Contains("write_memory"), "RPC only reads selected memory, never halts or writes");
    await stream.WriteAsync(Encoding.UTF8.GetBytes("OK 0x01 "));
    await Task.Delay(15);
    await stream.WriteAsync(Encoding.UTF8.GetBytes("2 3 4\x1a"));
}))
await using (var client = new OpenOcdMemoryClient())
{
    await client.ConnectAsync(listener.Port, CancellationToken.None);
    Check((await client.ReadAsync(0x20000010, 4, CancellationToken.None)).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "fragmented TCP frame assembled");
    await listener.Completion;
}

await using (var listener = new TestServer(async stream =>
{
    await TestServer.ReadFrame(stream);
    await Task.Delay(150);
}))
await using (var client = new OpenOcdMemoryClient())
{
    await client.ConnectAsync(listener.Port, CancellationToken.None);
    using var cancellation = new CancellationTokenSource(50);
    await Reject(async () => { await client.ReadAsync(0x20000010, 4, cancellation.Token); }, "cancelled in-flight RPC fails");
    await Reject(async () => { await client.ReadAsync(0x20000010, 4, CancellationToken.None); }, "late RPC cannot contaminate next read");
}

var source = new TestSource();
await using (var service = new OpenOcdPlotService(source, 16))
{
    await Reject(() => service.StartAsync(100), "empty channel start rejected");
    await service.AddAsync("counter", PlotScalar.UInt32);
    await Reject(() => service.AddAsync("counter", PlotScalar.UInt32), "duplicate channel rejected");
    await Reject(() => service.StartAsync(1), "unbounded sampling rate rejected");
    await service.StartAsync(20);
    await Reject(() => service.StartAsync(20), "concurrent start rejected");
    await Reject(() => service.AddAsync("second", PlotScalar.UInt32), "configuration frozen during capture");
    await WaitUntil(() => service.Snapshot().EvictedRecords > 0);
    var live = service.Snapshot();
    Check(live.Records.Length == 16 && live.Records[^1].Seconds > live.Records[0].Seconds, "bounded ring retains latest chronological records and counts evictions");
    live.Records[^1].Values[0] = -999;
    Check(service.Snapshot().Records[^1].Values[0] != -999, "snapshot cannot mutate retained data");
    source.Running = false;
    await WaitUntil(() => service.Snapshot().Records.Any(r => !r.TargetRunning));
    Check(service.Snapshot().Records[^1].Segment > 0, "halted samples explicitly tagged and curve split");
    await service.StopAsync();
    var count = source.Count;
    await Task.Delay(50);
    Check(source.Count == count && !service.Snapshot().Capturing, "stop drains current read and prevents further polling");
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
    var csv = service.ExportCsv();
    Check(csv.Contains("timestamp=host read completion") && csv.Contains("counter@0x20000010:UInt32") && csv.Contains("evicted_records="), "CSV includes source time, type, address and loss metadata");
    Check(!csv.Contains(";false;") && csv.Contains(",false,"), "CSV culture-independent values and state");
    source.DelayMs = 65;
    source.Running = true;
    await service.StartAsync(20);
    await WaitUntil(() => service.Snapshot().MissedIntervals > 0);
    await service.StopAsync();
    Check(service.Snapshot().MissedIntervals > 0, "slow probe exposes missed periods without backlog");
    source.DelayMs = 0;
    source.NonFinite = true;
    await service.StartAsync(20);
    await WaitUntil(() => service.Snapshot().InvalidRecords > 0);
    source.NonFinite = false;
    await WaitUntil(() => service.Snapshot().Records.Length > 0);
    await service.StopAsync();
    Check(service.Snapshot().Records.All(r => r.Values.All(double.IsFinite)) && service.Snapshot().Records[0].Segment > 0, "NaN is counted and creates a gap without breaking chart");
    source.Failure = "raw OpenOCD failure: target not halted";
    await service.StartAsync(20);
    await WaitUntil(() => service.Snapshot().Error is not null);
    Check(!service.Snapshot().Capturing && service.Snapshot().Error!.Contains(source.Failure), "target failure stops capture and retains raw error");
    source.Failure = null;
    await service.StartAsync(10000);
    await WaitUntil(() => service.Snapshot().Records.Length > 0);
    source.PlotSessionId = Guid.NewGuid();
    service.StopIfSessionEnded();
    await WaitUntil(() => !service.Snapshot().Capturing);
    await Reject(() => service.StartAsync(20), "session change invalidates resolved addresses");
    Check(service.Snapshot().Status.Contains("会话已结束"), "session end stops even a long-interval capture immediately");
    service.Clear(true);
    await service.AddAsync("counter", PlotScalar.UInt32);
    Check(service.Snapshot().Records.Length == 0 && service.Snapshot().Channels[0].SessionId == source.PlotSessionId, "new session resolves fresh addresses and clears old data");
}
if (args is ["--runtime", var runtime])
{
    foreach (var toolset in new[] { "arm.gnu", "agm.agrv", "wch.riscv" })
    {
        var reserve = new TcpListener(IPAddress.Loopback, 0);
        reserve.Start();
        var port = ((IPEndPoint)reserve.LocalEndpoint).Port;
        reserve.Stop();
        var executable = Path.Combine(runtime, "toolsets", toolset, "1.0.0", "openocd", "bin", "openocd.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        // noinit 不初始化任何探针/目标；只验证真实内置 JimTcl 和 RPC 分帧，内存由测试 proc 提供。
        foreach (var command in new[] { "noinit", "bindto 127.0.0.1", "gdb_port disabled", "telnet_port disabled", "tcl_port " + port,
            "proc studiox_test {cmd name width address count} {upvar $name bytes; for {set i 0} {$i < $count} {incr i} {set bytes($i) [expr {$i + 10}]}}",
            "set studiox_plot_target studiox_test" })
        { start.ArgumentList.Add("-c"); start.ArgumentList.Add(command); }
        using var process = Process.Start(start)!;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        async Task Drain(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lines.Enqueue(line);
                if (line.Contains($"Listening on port {port} for tcl connections")) { ready.TrySetResult(); }
            }
        }
        var drains = new[] { Drain(process.StandardOutput), Drain(process.StandardError) };
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using var client = new OpenOcdMemoryClient();
            await client.ConnectAsync(port, CancellationToken.None);
            Check((await client.ReadAsync(0x20000000, 4, CancellationToken.None)).SequenceEqual(new byte[] { 10, 11, 12, 13 }), toolset + " real Tcl RPC, synthetic SRAM, no hardware");
            Check((await client.ReadAsync(0x20000010, 1, CancellationToken.None)).SequenceEqual(new byte[] { 10 }), toolset + " repeated RPC clears old array entries");
        }
        catch { Console.WriteLine(string.Join('\n', lines)); throw; }
        finally
        {
            if (!process.HasExited) { process.Kill(true); }
            await process.WaitForExitAsync();
            await Task.WhenAll(drains);
        }
    }
}
Console.WriteLine($"PASS {checks.Count} checks; offline only, no probe connected.");

sealed class SymbolTransport : IGdbMiTransport
{
    public event Action<string>? RecordReceived { add { } remove { } }
    public List<string> Commands { get; } = [];
    public int LiveVariables { get; private set; }
    public string Address { get; set; } = "536870928";
    public Task<string> ExecuteAsync(string command, CancellationToken token = default)
    {
        Commands.Add(command);
        var split = command.IndexOf('-');
        var id = command[..split];
        var request = command[split..];
        if (request.StartsWith("-var-create"))
        {
            LiveVariables++;
            var type = request.EndsWith("\"::samples\"") ? "uint32_t [4]" : request.EndsWith("\"::pointer\"") ? "uint32_t *" : "volatile uint32_t";
            return Task.FromResult(id + "^done,name=\"var1\",numchild=\"0\",type=" + MiRecord.Quote(type));
        }
        if (request.StartsWith("-var-delete")) { LiveVariables--; return Task.FromResult(id + "^done"); }
        return Task.FromResult(id + "^done,value=" + MiRecord.Quote(request.Contains("sizeof") ? "4" : Address));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class TestSource : IOpenOcdPlotSource
{
    public Guid PlotSessionId { get; set; } = Guid.NewGuid();
    public bool CanPlot => true;
    public string PlotTarget => "OFFLINE TEST · no hardware";
    public int Count;
    public int DelayMs;
    public bool Running = true, NonFinite;
    public string? Failure;
    public Task<OpenOcdPlotChannel> ResolvePlotChannelAsync(string expression, PlotScalar type, CancellationToken token = default) =>
        Task.FromResult(new OpenOcdPlotChannel(PlotSessionId, expression, 0x20000010, type, true));
    public async Task<OpenOcdPlotReading> ReadPlotAsync(IReadOnlyList<OpenOcdPlotChannel> channels, CancellationToken token = default)
    {
        if (Failure is not null) { throw new IOException(Failure); }
        if (DelayMs > 0) { await Task.Delay(DelayMs, token); }
        if (channels.Any(c => c.SessionId != PlotSessionId)) { throw new IOException("session changed"); }
        var sample = Interlocked.Increment(ref Count);
        return new(DateTimeOffset.Now, Running, channels.Select(_ => NonFinite ? double.NaN : sample + .25).ToArray());
    }
}

sealed class TestServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    public int Port { get; }
    public Task Completion { get; }
    public TestServer(Func<NetworkStream, Task> serve)
    {
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Completion = Task.Run(async () => { using var client = await listener.AcceptTcpClientAsync(); await serve(client.GetStream()); });
    }
    public static async Task<string> ReadFrame(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        using var timeout = new CancellationTokenSource(3000);
        while (await stream.ReadAsync(one, timeout.Token) != 0 && one[0] != 0x1a) { bytes.Add(one[0]); }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
    public async ValueTask DisposeAsync() { listener.Stop(); await Completion; }
}
