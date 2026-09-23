using StudioX.Application.Serial;
using StudioX.Devices;
using System.IO.Ports;
using System.Text;
using System.Runtime.CompilerServices;

var checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
void Reject(Action action, string label) { try { action(); } catch (ArgumentException) { checks++; return; } throw new Exception(label); }
Check(SerialCodec.Encode("00 0x01 a5 FF", SerialTextMode.Hex, SerialLineEnding.None, false).SequenceEqual(new byte[] { 0, 1, 0xa5, 255 }), "HEX payload changed");
Reject(() => SerialCodec.Encode("A", SerialTextMode.Hex, SerialLineEnding.None, false), "Partial hex accepted");
Reject(() => SerialCodec.Encode("GG", SerialTextMode.Hex, SerialLineEnding.None, false), "Invalid hex accepted");
Reject(() => SerialCodec.Encode(new string('x', 262145), SerialTextMode.Utf8, SerialLineEnding.None, false), "Oversized input accepted");
Check(Encoding.UTF8.GetString(SerialCodec.Encode("中文", SerialTextMode.Utf8, SerialLineEnding.CrLf, false)) == "中文\r\n", "UTF8/CRLF mismatch");
Check(Convert.ToHexString(SerialCodec.Encode("中文", SerialTextMode.Gb2312, SerialLineEnding.None, false)) == "D6D0CEC4", "GB2312 mismatch");
Reject(() => SerialCodec.Encode("😀", SerialTextMode.Gb2312, SerialLineEnding.None, false), "Unrepresentable GB2312 silently replaced");
Check(Encoding.UTF8.GetString(SerialCodec.Encode(@"\e[31m红\e[0m\r\n", SerialTextMode.Utf8, SerialLineEnding.None, true)) == "\x1b[31m红\x1b[0m\r\n", "ANSI escapes mismatch");
Reject(() => new SerialSettings("COM15", 0).Validate(), "Bad baud accepted");
Reject(() => new SerialSettings("COM15", StopBits: StopBits.OnePointFive).Validate(), "8/1.5 bits accepted");
foreach (var flow in Enum.GetValues<Handshake>()) { new SerialSettings("COM15", FlowControl: flow).Validate(); checks++; }
var now = DateTimeOffset.UtcNow;
var terminal = new AnsiTerminal();
terminal.Feed("\x1b[3", now, true); terminal.Feed("1mRED\x1b[0m plain\r\n中文", now, true);
var snapshot = terminal.Snapshot(false);
Check(snapshot.Text == "RED plain\n中文", "ANSI split or CRLF failed");
Check(snapshot.Spans[0].Style.Foreground == 0xCD3131 && snapshot.Spans[1].Style.Foreground is null, "SGR reset failed");
terminal.Clear(); terminal.Feed("\x1b[38;2;12;34;56mRGB\x1b[48;5;196mX\x1b[1;3;4m!", now, true);
snapshot = terminal.Snapshot(false);
Check(snapshot.Spans[0].Style.Foreground == 0x0c2238 && snapshot.Spans[^1].Style.Background == 0xff0000, "RGB/256 color failed");
Check(snapshot.Spans[^1].Style is { Bold: true, Italic: true, Underline: true }, "Text attributes failed");
terminal.Clear(); terminal.Feed("\x1b[38:2::12:34:56mRGB", now, true);
Check(terminal.Snapshot(false).Spans[0].Style.Foreground == 0x0c2238, "Colon RGB color failed");
terminal.Clear(); terminal.Feed("progress 100%\rOK\x1b[K\nabc\bX", now, true);
Check(terminal.Snapshot(false).Text == "OK\nabX", "CR/erase/backspace failed");
terminal.Clear(); terminal.Feed("A\x1b]52;c;malicious", now, true); terminal.Feed("\x1b\\B", now, true);
Check(terminal.Snapshot(false).Text == "AB", "OSC escaped into visible output");
terminal.Clear(); terminal.Feed("\x1b[31mRAW", now, false);
Check(terminal.Snapshot(false).Text == @"\x1B[31mRAW", "ANSI raw display failed");
terminal.Clear(); for (var i = 0; i < 2200; i++) terminal.Feed(i + "\r\n", now, true);
Check(terminal.Snapshot(false).TrimmedLines > 0 && terminal.Snapshot(false).Text.Split('\n').Length <= 2000, "Scrollback limit failed");
foreach (var mode in new[] { SerialTextMode.Utf8, SerialTextMode.Gb2312 })
{
    var encoding = SerialCodec.EncodingFor(mode); var decoder = encoding.GetDecoder(); var actual = new StringBuilder();
    foreach (var value in encoding.GetBytes("跨包中文ABC")) { var chars = new char[4]; var n = decoder.GetChars([value], 0, 1, chars, 0, false); actual.Append(chars, 0, n); }
    Check(actual.ToString() == "跨包中文ABC", "Split multibyte decoder failed");
}
await using (var hub = new DeviceHub())
{
    var fake = new FakeTransport(); var session = await hub.OpenAsync(fake);
    using var observer = session.Subscribe(); using var timeout = new CancellationTokenSource(2000);
    var first = await observer.Reader.ReadAsync(timeout.Token);
    Check(first.Payload.Span.SequenceEqual(new byte[] { 0xaa, 0x55 }), "Startup frame lost before subscriber");
    await session.SendAsync(new byte[] { 0, 255 }); Check(fake.Sent.SequenceEqual(new byte[] { 0, 255 }), "Binary TX changed");
    var rejected = false; try { await hub.OpenAsync(new FakeTransport()); } catch { rejected = true; }
    Check(rejected, "Duplicate connection owner accepted");
    await session.DisposeAsync(); Check(fake.Closed, "Connection not released");
    await using var again = await hub.OpenAsync(new FakeTransport()); checks++;
}
var fixture = Path.Combine(Path.GetTempPath(), "StudioX-SerialChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
await using (var hub = new DeviceHub())
{
    StreamTransport? stream = null;
    await using var service = new SerialTerminalService(hub, fixture, _ => stream = new StreamTransport());
    await service.ConnectAsync(new("COM15"));
    var packet = Encoding.UTF8.GetBytes("\x1b[32m跨包中文\x1b[0m\r\n");
    foreach (var value in packet) stream!.Push([value]);
    await WaitFor(() => service.Status.Received == packet.Length);
    var output = service.ReadDisplay(-1, false)!;
    Check(output.Text == "跨包中文\n" && output.Spans[0].Style.Foreground == 0x0DBC79, "Service split UTF8/ANSI failed");
    await service.SetDisplayAsync(SerialTextMode.Hex, true, false);
    Check(service.ReadDisplay(-1, false)!.Text.Contains("1B") && service.Status.Received == packet.Length, "Mode replay changed byte count");
    await service.SetDisplayAsync(SerialTextMode.Utf8, true, false);
    Check(service.ReadDisplay(-1, false)!.Text == "跨包中文\n", "Raw history replay failed");
    await service.SendAsync([0, 0x7f, 0x80, 0xff]);
    Check(service.Status.Sent == 4 && stream!.Sent.SequenceEqual(new byte[] { 0, 0x7f, 0x80, 0xff }), "Service binary TX failed");
    stream!.TimeoutSend = true; var sendFailed = false;
    try { await service.SendAsync([0xa5]); } catch (IOException ex) { sendFailed = ex.Message.Contains("部分字节"); }
    Check(sendFailed && service.Status.Sent == 4, "Timed out send counted as success"); stream.TimeoutSend = false;
    await service.ExportAsync(Path.Combine(fixture, "capture.bin"));
    Check((await File.ReadAllBytesAsync(Path.Combine(fixture, "capture.bin"))).SequenceEqual(packet), "Raw export included TX or changed RX");
    await service.ExportAsync(Path.Combine(fixture, "capture.jsonl"));
    Check((await File.ReadAllTextAsync(Path.Combine(fixture, "capture.jsonl"))).Contains("\"direction\":\"TX\""), "TX absent from JSONL export");
    service.Clear(); Check(service.ReadDisplay(-1, false)!.Text == "" && service.Status.Received == packet.Length, "Clear reset counters");
    await service.SetDisplayAsync(SerialTextMode.Gb2312, true, false);
    var gb = SerialCodec.Encode("中文", SerialTextMode.Gb2312, SerialLineEnding.None, false);
    foreach (var value in gb) stream!.Push([value]);
    await WaitFor(() => service.Status.Received == packet.Length + gb.Length);
    Check(service.ReadDisplay(-1, false)!.Text == "中文", "Service split GB2312 failed");
    var clock = System.Diagnostics.Stopwatch.StartNew(); await service.DisconnectAsync();
    Check(clock.ElapsedMilliseconds < 1000 && !service.Status.Connected && stream!.Closed, "Service disconnect stuck");
    await service.ConnectAsync(new("COM15")); service.Clear(); await service.SetDisplayAsync(SerialTextMode.Utf8, true, false);
    var countBefore = service.Status.Received; stream!.Push([0xe4]); await WaitFor(() => service.Status.Received == countBefore + 1);
    await service.DisconnectAsync(); Check(service.ReadDisplay(-1, false)!.Text.Contains('\ufffd'), "Incomplete final character lost");
    await service.SetDisplayAsync(SerialTextMode.Hex, false, false); await service.SetDisplayAsync(SerialTextMode.Utf8, true, false);
    Check(service.ReadDisplay(-1, false)!.Text.Contains('\ufffd'), "Session boundary lost during replay");
    await service.ConnectAsync(new("COM15")); stream!.Fail(); await WaitFor(() => !service.Status.Connected);
    Check(service.Status.Message.Contains("中断"), "Device removal not reported");
    await service.DisconnectAsync(); await service.ConnectAsync(new("COM15")); checks++;
    await service.SavePreferencesAsync(new(new("COM15", Dtr: true, Rts: true), History: [".ansi"]));
    var saved = await service.LoadPreferencesAsync(); Check(!saved.Connection.Dtr && !saved.Connection.Rts, "Control outputs restored asserted");
    await service.Protocol.ConfigureAsync(new(SerialProtocol.ModbusRtu));
    stream!.Push(ModbusRtuDecoder.WithCrc([1, 3, 2, 0, 42]));
    await WaitFor(() => service.Protocol.Snapshot()!.Frames.Any(f => f.Status == "CRC 正确"));
    Check(service.Status.Connected, "Protocol observation disconnected serial owner");
    await service.SendAsync(Convert.FromHexString("010300000002C40B"));
    await WaitFor(() => service.Protocol.Snapshot()!.Frames.Any(f => f.Direction == "TX"));
    Check(stream.Sent.Length == 8, "Protocol TX observer changed wire bytes");
    var offlineRejected = false; try { service.AnalyzeOffline([1]); } catch (InvalidOperationException) { offlineRejected = true; }
    Check(offlineRejected, "Offline input accepted during live capture");
    await service.DisconnectAsync();
}
checks += await ProtocolChecks.RunAsync(fixture);
Console.WriteLine($"PASS: {checks} serial, Modbus RTU, script host, stream and lifecycle checks.");
Console.WriteLine("Fixtures: " + fixture);
static async Task WaitFor(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(3000);
    while (!condition()) await Task.Delay(5, timeout.Token);
}

sealed class FakeTransport : IDeviceTransport
{
    public string Key => "serial:test"; public bool IsSimulated => true; public byte[] Sent = []; public bool Closed;
    public ValueTask OpenAsync(CancellationToken token) => ValueTask.CompletedTask;
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken token) { yield return new byte[] { 0xaa, 0x55 }; await Task.Delay(Timeout.Infinite, token); }
    public ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token) { Sent = bytes.ToArray(); return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() { Closed = true; return ValueTask.CompletedTask; }
}

sealed class StreamTransport : IDeviceTransport
{
    private readonly System.Threading.Channels.Channel<ReadOnlyMemory<byte>> channel = System.Threading.Channels.Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    public string Key => "serial:COM15"; public bool IsSimulated => true; public byte[] Sent = []; public bool Closed; public bool TimeoutSend;
    public void Push(byte[] bytes) => channel.Writer.TryWrite(bytes);
    public void Fail() => channel.Writer.TryComplete(new IOException("Simulated device removed"));
    public ValueTask OpenAsync(CancellationToken token) => ValueTask.CompletedTask;
    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken token) => channel.Reader.ReadAllAsync(token);
    public ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token) { if (TimeoutSend) throw new TimeoutException(); Sent = bytes.ToArray(); return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() { Closed = true; channel.Writer.TryComplete(); return ValueTask.CompletedTask; }
}
