using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using StudioX.Application.SerialPlot;
using StudioX.Devices;

var checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
void Reject(Action action, string label)
{
    try { action(); } catch (ArgumentException) { checks++; return; }
    throw new Exception(label);
}
(List<double[]> Rows, List<string> Errors) Parse(PlotFormat format, string input, int chunk)
{
    var parser = new PlotStreamParser(format); var rows = new List<double[]>(); var errors = new List<string>();
    var bytes = Encoding.UTF8.GetBytes(input);
    for (var i = 0; i < bytes.Length; i += chunk)
        parser.Feed(bytes.AsSpan(i, Math.Min(chunk, bytes.Length - i)), (r, e) => { if (r is not null) rows.Add(r); else errors.Add(e!); });
    return (rows, errors);
}
foreach (var chunk in new[] { 1, 2, 3, 7, 64, 4096 })
{
    var p = Parse(new(), "\uFEFF4095,1024\r\n-1.5,2e3\n.1,-.2\n", chunk);
    Check(p.Rows.Count == 3 && p.Errors.Count == 0 && p.Rows[1][1] == 2000, "split numeric records / BOM / CRLF");
    p = Parse(new("分", "结束"), "1分2结束3分4结束", chunk);
    Check(p.Rows.Count == 2 && p.Rows[1][0] == 3 && p.Errors.Count == 0, "split Unicode delimiters");
    p = Parse(new("||", "<end>"), "-2||1e-5<end>4||5<end>", chunk);
    Check(p.Rows.Count == 2 && p.Rows[0][1] == 1e-5, "multichar delimiters");
}
Check(Parse(new("\\t", "\\r\\n"), "1\t2\r\n3\t4\r\n", 1).Rows.Count == 2, "escaped tab / CRLF");
Check(Parse(new(" ", ";"), "1 2;3 4;", 1).Rows.Count == 2, "literal space / semicolon");
Check(Parse(new(), "1\n2\n", 1).Rows.Count == 2, "single channel");
var invalid = Parse(new(), "1,2\nNaN,3\nInfinity,4\n1e999,2\n1,,2\n4\nword,2\n,2\n1e101,2\n3,4\n", 2);
Check(invalid.Rows.Count == 2 && invalid.Errors.Count == 8, "invalid / empty / channel mismatch / overflow");
var channels = string.Join(',', Enumerable.Range(1, 16));
Check(Parse(new(), channels + "\n", 3).Rows[0].Length == 16, "16 channels");
Check(Parse(new(), channels + ",17\n", 3).Errors.Count == 1, "channel cap");
Check(Parse(new(), "123,456", 2).Rows.Count == 0, "unterminated record held");
var oversized = Parse(new(), new string('9', 50000) + "\n1,2\n", 13);
Check(oversized.Errors.Count == 1 && oversized.Rows.Count == 1, "overlong recovery bounded");
Reject(() => new PlotFormat("", "\\n").Validate(), "empty delimiter");
Reject(() => new PlotFormat(",", ",").Validate(), "identical delimiters");
Reject(() => new PlotFormat("\\r", "\\r\\n").Validate(), "overlap delimiters");
Reject(() => new PlotFormat("\\x", "\\n").Validate(), "bad escape");
Reject(() => new PlotFormat(",", "\\").Validate(), "trailing slash");
Reject(() => new PlotFormat(SampleIntervalMs: double.NaN).Validate(), "invalid timebase");
var recovering = new PlotStreamParser(new()); var recovered = new List<double[]>();
void Capture(double[]? row, string? _) { if (row is not null) recovered.Add(row); }
recovering.Feed("123,"u8, Capture); recovering.LoseSynchronization();
recovering.Feed("456\n7,8\n"u8, Capture);
Check(recovered.Count == 1 && recovered[0][0] == 7, "loss cannot join partial records");
recovering.Feed(new byte[] { 0xff }, Capture); recovering.Feed("0,0\n9,10\n"u8, Capture);
Check(recovered.Count == 2 && recovered[1][0] == 9, "invalid UTF8 resync");

var reduced = PlotGeometry.Reduce(Enumerable.Range(0, 10000).Select(i => new PlotSample(i * .001, [i == 4321 ? 4095 : 0], 0)).ToArray(), 0, new(0, 10), 100);
Check(reduced.Count <= 400 && reduced.Any(p => p.Value == 4095), "decimation keeps narrow spike");
var gap = PlotGeometry.Reduce([new(0, [1], 0), new(.001, [2], 1), new(.002, [3], 2)], 0, new(0, 10), 10);
Check(gap.Select(p => p.Segment).Distinct().Count() == 3, "decimation keeps discontinuities in same bucket");
var range = new PlotRange(10, 20).Zoom(.5, .25, .001, 3600000);
Check(range == new PlotRange(11.25, 16.25), "zoom anchored at pointer");
for (var i = 0; i < 1000; i++) range = range.Zoom(.8, .5, .001, 3600000);
Check(range.Span >= .000999, "zoom lower bound");
var extreme = new PlotRange(1e99, 1e100);
for (var i = 0; i < 1000; i++) extreme = extreme.Zoom(.8, .5, 1e-12, 2e100);
Check(extreme.Span > 0 && double.IsFinite(extreme.Span), "extreme value zoom cannot collapse");

var testDirectory = Path.Combine(Path.GetTempPath(), "StudioX-SerialPlotChecks-" + Guid.NewGuid().ToString("N"));
if (!Path.GetFullPath(testDirectory).StartsWith(Path.Combine(Path.GetFullPath(Path.GetTempPath()), "StudioX-SerialPlotChecks-"), StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Unexpected test directory.");
Directory.CreateDirectory(testDirectory);
try
{
    await using var hub = new DeviceHub();
    FakeTransport? transport = null;
    await using var service = new SerialPlotService(hub, testDirectory, _ => transport = new FakeTransport());
    async Task Wait(Func<PlotSnapshot, bool> predicate)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!predicate(service.Snapshot()))
        {
            if (DateTime.UtcNow > until) throw new Exception("Timed out: " + service.Snapshot());
            await Task.Delay(10);
        }
    }
    await service.ConnectAsync(new("COM123"), new(SampleIntervalMs: 10));
    // 实机复现：开口恰好落在第二个字段，残缺首行仍是合法数字，不能用来锁定通道数。
    await transport!.Push("2\n1,2\nbad,3\n4,5\n"); await Wait(s => s.ValidRecords == 2);
    var snapshot = service.Snapshot();
    Check(snapshot.Channels == 2 && snapshot.Samples[0].Values.SequenceEqual(new double[] { 1, 2 }), "initial partial row cannot lock wrong channel count");
    Check(snapshot.Samples[0].Seconds == 0 && snapshot.Samples[1].Seconds == .02, "fixed sampling preserves invalid row duration");
    Check(snapshot.Samples[0].Segment != snapshot.Samples[1].Segment && snapshot.InvalidRecords == 1, "invalid record breaks curve");
    service.Clear(); await transport.Push("partial\n10,20\n"); await Wait(s => s.ValidRecords == 1);
    Check(service.Snapshot().InvalidRecords == 0, "clear resynchronizes and resets counters");
    var many = new StringBuilder();
    for (var i = 0; i < SerialPlotService.HistoryCapacity + 500; i++) many.Append(i).Append(",2\n");
    await transport.Push(many.ToString()); await Wait(s => s.ValidRecords == SerialPlotService.HistoryCapacity + 501);
    snapshot = service.Snapshot();
    Check(snapshot.Samples.Length == SerialPlotService.HistoryCapacity && snapshot.EvictedRecords == 501, "bounded history visible eviction");
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
    var csv = Path.Combine(testDirectory, "plot.csv"); await SerialPlotService.ExportCsvAsync(csv, snapshot);
    var exported = await File.ReadAllLinesAsync(csv);
    Check(exported.Length == SerialPlotService.HistoryCapacity + 2 && exported[1] == "time_s,segment,CH1,CH2" && exported[^1].Split(',').Length == 4, "CSV invariant culture / labels");
    CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    await service.SavePreferencesAsync(new(new("COM123", Dtr: true, Rts: true), new(";", "\\r\\n", 10)));
    var preferences = await service.LoadPreferencesAsync();
    Check(preferences.Format.FieldSeparator == ";" && !preferences.Connection.Dtr && !preferences.Connection.Rts, "persist format and safe control line defaults");
    await service.DisconnectAsync(); Check(transport.Disposed && !service.Snapshot().Connected, "stop disposes transport");
    await service.ConnectAsync(new("COM123"), new()); await transport!.Push("6\n5,6\n"); await Wait(s => s.ValidRecords == 1);
    Check(service.Snapshot().Channels == 2 && service.Snapshot().InvalidRecords == 0, "reconnect resynchronizes without reporting discarded fragment as an error");
    await Task.Delay(25); await transport.Push("7,8\n"); await Wait(s => s.ValidRecords == 2);
    snapshot = service.Snapshot(); Check(snapshot.Samples[1].Seconds > snapshot.Samples[0].Seconds, "host monotonic clock");
    transport.Fail(new IOException("simulated unplug")); await Wait(s => !s.Connected);
    Check(service.Snapshot().LastError!.Contains("simulated unplug", StringComparison.Ordinal), "unplug diagnostic preserved");
    await service.DisconnectAsync();
    await using (var owner = await hub.OpenAsync(new FakeTransport()))
    {
        var failed = false;
        try { await service.ConnectAsync(new("COM123"), new()); } catch (Exception ex) when (ex.Message.Contains("占用", StringComparison.Ordinal)) { failed = true; }
        Check(failed, "cannot steal another tool's connection");
    }
    await service.ConnectAsync(new("COM123"), new()); await service.DisconnectAsync();
    Check(!service.Snapshot().Connected, "reconnect after unplug and ownership release");
    await service.ConnectAsync(new("COM123"), new("||", "<end>"));
    await transport!.Push("2<en"); await transport.Push("d>3||4<end>"); await Wait(s => s.ValidRecords == 1);
    Check(service.Snapshot().Channels == 2 && service.Snapshot().Samples[0].Values[0] == 3, "initial synchronization spans multichar terminator split across packets");
    await service.DisconnectAsync();
    await service.ConnectAsync(new(), new(), demo: true); await Wait(s => s.ValidRecords > 2);
    Check(service.Snapshot().Simulated && service.Snapshot().Channels == 3, "demo clearly identified and parsed through same service");
    await service.DisconnectAsync();
}
finally { Directory.Delete(testDirectory, true); }
Console.WriteLine($"PASS: {checks} serial plot checks");

sealed class FakeTransport : IDeviceTransport
{
    private readonly Channel<ReadOnlyMemory<byte>> input = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    public string Key => "serial:COM123";
    public bool IsSimulated => false;
    public bool Disposed { get; private set; }
    public ValueTask OpenAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask Push(string text) => input.Writer.WriteAsync(Encoding.UTF8.GetBytes(text));
    public void Fail(Exception error) => input.Writer.TryComplete(error);
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    { await foreach (var frame in input.Reader.ReadAllAsync(cancellationToken)) yield return frame; }
    public ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask DisposeAsync() { Disposed = true; input.Writer.TryComplete(); return ValueTask.CompletedTask; }
}
