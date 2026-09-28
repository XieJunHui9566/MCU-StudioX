using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using StudioX.Devices;
using StudioX.Packages;

/// <summary>只在临时目录启动 CPython，模拟分片 USB 收发；无串口访问。</summary>
internal sealed class FixtureTransport(string python, string root, MicroPythonProfile profile) : IDeviceTransport
{
    private readonly Channel<ReadOnlyMemory<byte>> incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    private readonly List<byte> code = [];
    private Process? worker;
    public string Key => "serial:COM77";
    public bool IsSimulated => true;
    public bool Closed
    {
        get; private set;
    }
    public bool CorruptNextAck
    {
        get; set;
    }
    public bool CorruptTemporaryHash
    {
        get; set;
    }
    public bool WrongIdentity
    {
        get; set;
    }
    public bool HangNext
    {
        get; set;
    }
    public bool RunForeverNext { get; set; }
    public int Interrupts { get; private set; }
    public void StreamOutput(string text) => incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
    public void FinishRun() => Reply("\x04\x04>"u8.ToArray());
    public List<string> Commands { get; } = [];
    public ValueTask OpenAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8
        };
        // RP2 文本默认 UTF-8；Windows CPython 的系统代码页不能充当该默认值。
        start.ArgumentList.Add("-X");
        start.ArgumentList.Add("utf8");
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "device_worker.py"));
        worker = Process.Start(start)!;
        return ValueTask.CompletedTask;
    }
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var bytes in incoming.Reader.ReadAllAsync(cancellationToken))
        {
            yield return bytes;
        }
    }
    public async ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        foreach (var value in bytes.ToArray())
        {
            if (value == 1)
            {
                Reply("raw REPL; CTRL-B to exit\r\n>"u8.ToArray());
            }
            else if (value == 3 || value == 2)
            {
                if (value == 3) { Interrupts++; }
                code.Clear();
            }
            else if (value == 4)
            {
                var text = Encoding.UTF8.GetString(code.ToArray());
                code.Clear();
                Commands.Add(text);
                if (HangNext)
                {
                    HangNext = false;
                    continue;
                }
                if (CorruptNextAck)
                {
                    CorruptNextAck = false;
                    Reply("NO"u8.ToArray());
                    continue;
                }
                if (RunForeverNext)
                {
                    RunForeverNext = false;
                    Reply(Encoding.UTF8.GetBytes("OK中文持续输出"));
                    continue;
                }
                string output;
                var error = "";
                if (text.Contains("sys.implementation._machine", StringComparison.Ordinal))
                {
                    output = JsonSerializer.Serialize(new object[] { "micropython", new[] { 1, 29, 0 }, WrongIdentity ? "Another board" : profile.ExpectedMachine }) + "\r\n";
                }
                else
                {
                    await worker!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        code = text
                    }).AsMemory(), cancellationToken);
                    await worker.StandardInput.FlushAsync(cancellationToken);
                    var line = await worker.StandardOutput.ReadLineAsync(cancellationToken) ?? throw new InvalidOperationException(await worker.StandardError.ReadToEndAsync(cancellationToken));
                    using var json = JsonDocument.Parse(line);
                    output = json.RootElement.GetProperty("output").GetString()!;
                    error = json.RootElement.GetProperty("error").GetString()!;
                    if (CorruptTemporaryHash && text.Contains("_sx_hash.digest()", StringComparison.Ordinal) && text.Contains(".tmp", StringComparison.Ordinal))
                    {
                        output = new string('0', 64);
                    }
                }
                Reply(Encoding.UTF8.GetBytes("OK" + output + "\x04" + error + "\x04>"));
            }
            else if (value != 13 || code.Count > 0)
            {
                code.Add(value);
            }
        }
    }
    private void Reply(byte[] bytes)
    {
        // 刻意拆开 UTF-8 字符、OK 和结束符，验证接收边界不依赖串口分包。
        for (var i = 0; i < bytes.Length; i += 3)
        {
            incoming.Writer.TryWrite(bytes.AsMemory(i, Math.Min(3, bytes.Length - i)));
        }
    }
    public async ValueTask DisposeAsync()
    {
        Closed = true;
        incoming.Writer.TryComplete();
        if (worker is { } process)
        {
            process.StandardInput.Close();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync();
            process.Dispose();
            worker = null;
        }
    }
}
