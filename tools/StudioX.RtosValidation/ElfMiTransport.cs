using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using StudioX.Engine.Debugging;

/// <summary>只加载本地 ELF 文件的验证传输；从不创建 probe、socket 或 inferior。</summary>
internal sealed class ElfMiTransport : IGdbMiTransport
{
    private readonly Process process;
    private readonly Task reader;
    private readonly Task stderr;
    private readonly Dictionary<int, TaskCompletionSource<string>> pending = [];
    private readonly object sync = new();
    public List<string> Commands { get; } = [];
    public List<string> Records { get; } = [];
    public event Action<string>? RecordReceived;

    public ElfMiTransport(string executable)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--nx"); start.ArgumentList.Add("--nh"); start.ArgumentList.Add("--interpreter=mi2");
        process = Process.Start(start) ?? throw new InvalidOperationException("GDB failed to start.");
        reader = DrainAsync(process.StandardOutput, false);
        stderr = DrainAsync(process.StandardError, true);
    }

    private async Task DrainAsync(StreamReader stream, bool isError)
    {
        try
        {
            while (await stream.ReadLineAsync() is { } line)
            {
                lock (sync) Records.Add((isError ? "stderr " : "") + line);
                if (!isError && Regex.Match(line, @"^(\d+)\^") is { Success: true } match)
                {
                    var id = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    TaskCompletionSource<string>? completion;
                    lock (sync) pending.Remove(id, out completion);
                    completion?.TrySetResult(line);
                }
                else if (!isError && line.Trim() != "(gdb)") RecordReceived?.Invoke(line);
            }
        }
        finally
        {
            lock (sync)
            {
                foreach (var item in pending.Values) item.TrySetException(new IOException("GDB exited before replying."));
                pending.Clear();
            }
        }
    }

    public async Task<string> ExecuteAsync(string command, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var split = command.IndexOf('-');
        var id = int.Parse(command.AsSpan(0, split), CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync) { pending.Add(id, completion); Commands.Add(command[split..]); }
        try
        {
            await process.StandardInput.WriteLineAsync(command.AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        }
        finally { lock (sync) pending.Remove(id); }
    }

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        await Task.WhenAll(reader, stderr);
        process.Dispose();
    }
}

internal sealed class FaultMiTransport(IGdbMiTransport inner, Func<string, string?> response) : IGdbMiTransport
{
    public List<string> Commands { get; } = [];
    public event Action<string>? RecordReceived { add => inner.RecordReceived += value; remove => inner.RecordReceived -= value; }
    public async Task<string> ExecuteAsync(string command, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var split = command.IndexOf('-');
        var text = command[split..]; Commands.Add(text);
        if (response(text) is { } injected) return command[..split] + injected;
        return await inner.ExecuteAsync(command, token);
    }
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

/// <summary>让首条读取保持未完成，复现用户在 MI 等待期间取消；不模拟或触发任何目标动作。</summary>
internal sealed class DelayedMiTransport(IGdbMiTransport inner) : IGdbMiTransport
{
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool held;
    public bool Enabled { get; set; }
    public bool Disposed { get; private set; }
    public CancellationToken? HeldToken { get; private set; }
    public Task Entered => entered.Task;
    public List<string> Commands { get; } = [];
    public event Action<string>? RecordReceived { add => inner.RecordReceived += value; remove => inner.RecordReceived -= value; }
    public void CompleteRead() => release.TrySetResult();
    public async Task<string> ExecuteAsync(string command, CancellationToken token = default)
    {
        var text = command[command.IndexOf('-')..]; Commands.Add(text);
        if (Enabled && !held && text.StartsWith("-data-evaluate-expression ", StringComparison.Ordinal))
        {
            held = true; HeldToken = token;
            var pendingRead = inner.ExecuteAsync(command, token);
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return await pendingRead;
        }
        return await inner.ExecuteAsync(command, token);
    }
    public ValueTask DisposeAsync() { Disposed = true; return inner.DisposeAsync(); }
}
