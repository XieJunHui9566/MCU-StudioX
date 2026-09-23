namespace StudioX.Engine.Debugging;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using StudioX.Foundation;

/// <summary>拥有 OpenOCD 和 MI 子进程；响应按 token 配对，通知异步交给应用层。</summary>
public sealed class GdbProcessTransport : IGdbMiTransport
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> pending = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly List<Task> readers = [];
    private readonly ProbeLease lease;
    private readonly DebugProcessJob job;
    private readonly StreamWriter log;
    private readonly object logSync = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource detached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? openocd, gdb;
    private long logCharacters;
    private int disposed, faulted;
    private bool closing;
    private volatile bool targetRunning, targetConnected;
    public event Action<string>? RecordReceived;
    private GdbProcessTransport(string logPath)
    {
        lease = ProbeLease.Acquire();
        try { job = new(); log = new(logPath, false, new UTF8Encoding(false)) { AutoFlush = true }; }
        catch { job?.Dispose(); lease.Dispose(); throw; }
    }
    public static async Task<(GdbProcessTransport Transport, OpenOcdDebugPlan Plan)> StartAsync(HardwareDebugPreparation preparation, CancellationToken token)
    {
        // 只连接本次进程实际声明成功监听的端口，绝不复用已运行的未知 GDB server。
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var plan = OpenOcdDebugPlanner.Create(preparation.ProjectDirectory, preparation.Configuration, preparation.Tools, preparation.Elf, port);
        var transport = new GdbProcessTransport(preparation.LogPath);
        try
        {
            transport.openocd = transport.Launch(plan.OpenOcd, plan.OpenOcdArguments, preparation);
            transport.readers.Add(transport.DrainAsync(transport.openocd.StandardOutput, "OpenOCD", port));
            transport.readers.Add(transport.DrainAsync(transport.openocd.StandardError, "OpenOCD", port));
            transport.readers.Add(transport.ObserveExitAsync(transport.openocd, "OpenOCD"));
            await transport.ready.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            transport.gdb = transport.Launch(plan.Gdb, plan.GdbArguments, preparation);
            transport.readers.Add(transport.DrainAsync(transport.gdb.StandardOutput, "GDB", port));
            transport.readers.Add(transport.DrainAsync(transport.gdb.StandardError, "GDB stderr", port));
            transport.readers.Add(transport.ObserveExitAsync(transport.gdb, "GDB"));
            return (transport, plan);
        }
        catch { await transport.DisposeAsync(); throw; }
    }
    private Process Launch(string executable, string[] arguments, HardwareDebugPreparation preparation)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = preparation.ProjectDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var name in ToolsetEnvironment.AmbientVariables) start.Environment.Remove(name);
        foreach (var (name, value) in ToolsetEnvironment.Create(preparation.Tools)) start.Environment[name] = value;
        var process = Process.Start(start) ?? throw new StudioXException("DEBUG_PROCESS", "无法启动 " + executable);
        try { job.Add(process); }
        catch { try { if (!process.HasExited) process.Kill(true); } finally { process.Dispose(); } throw; }
        WriteLog($"START {process.Id}: {executable}"); return process;
    }
    private async Task DrainAsync(StreamReader stream, string channel, int port)
    {
        try
        {
            while (await stream.ReadLineAsync() is { } line)
            {
                if (line.Length > 1024 * 1024) throw new IOException("调试器输出记录过长。");
                WriteLog(channel + " < " + line);
                if (channel == "OpenOCD" && line.Contains($"Listening on port {port} for gdb connections", StringComparison.Ordinal)) ready.TrySetResult();
                if (channel == "OpenOCD" && line.Contains("STUDIOX_DETACHED_RUNNING", StringComparison.Ordinal)) detached.TrySetResult();
                if (channel != "GDB") { RecordReceived?.Invoke("&" + MiRecord.Quote(channel + ": " + line)); continue; }
                if (line.Length == 0 || line.Trim() == "(gdb)") continue;
                var record = MiRecord.Parse(line);
                if (record.Kind == '*') { if (record.Class == "running") targetRunning = true; else if (record.Class == "stopped") targetRunning = false; }
                if (record.Kind == '^' && record.Token is { } id && pending.TryRemove(id, out var completion)) completion.TrySetResult(line);
                else RecordReceived?.Invoke(line);
            }
        }
        catch (Exception ex) { Fail(ex); }
    }
    private async Task ObserveExitAsync(Process process, string name)
    {
        await process.WaitForExitAsync();
        if (!closing) Fail(new StudioXException("DEBUG_PROCESS_EXIT", $"{name} 意外退出，退出代码：{process.ExitCode}。请查看调试日志。"));
    }
    private void Fail(Exception exception)
    {
        if (closing || Interlocked.Exchange(ref faulted, 1) != 0) return;
        WriteLog(exception.ToString()); ready.TrySetException(exception);
        foreach (var (id, item) in pending) if (pending.TryRemove(id, out _)) item.TrySetException(exception);
        RecordReceived?.Invoke("=studiox-transport-error,msg=" + MiRecord.Quote(exception.Message));
    }
    public async Task<string> ExecuteAsync(string command, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (faulted != 0 || gdb is null || gdb.HasExited) throw new StudioXException("DEBUG_DISCONNECTED", "调试器已断开，请结束会话后重新连接。");
        var separator = command.IndexOf('-');
        if (separator <= 0 || !int.TryParse(command.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || command.Contains('\n') || command.Contains('\r'))
            throw new ArgumentException("Invalid MI command.");
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate MI token.");
        try
        {
            await writes.WaitAsync(token);
            try { WriteLog("GDB > " + command); await gdb.StandardInput.WriteLineAsync(command.AsMemory(), token); await gdb.StandardInput.FlushAsync(token); }
            finally { writes.Release(); }
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
            if (command.Contains("-target-select ", StringComparison.Ordinal) && MiRecord.Parse(result).Class == "connected") targetConnected = true;
            return result;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
        { Fail(ex); throw; }
        finally { pending.TryRemove(id, out _); }
    }
    private void WriteLog(string text)
    {
        lock (logSync)
        {
            if (logCharacters > 20 * 1024 * 1024) return;
            log.WriteLine($"[{DateTimeOffset.Now:O}] {text}"); logCharacters += text.Length;
            if (logCharacters > 20 * 1024 * 1024) log.WriteLine("[会话日志达到 20 MiB，后续记录省略]");
        }
    }
    public async ValueTask DisposeAsync()
    {
        Exception? detachFailure = null;
        // 先停止命令、移除断点并 detach；结束调试后目标恢复运行。
        if (disposed == 0 && faulted == 0 && targetConnected)
        {
            closing = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                if (targetRunning)
                {
                    CheckResponse(await ExecuteAsync("2000000000-exec-interrupt --all", timeout.Token));
                    while (targetRunning) await Task.Delay(20, timeout.Token);
                }
                CheckResponse(await ExecuteAsync("2000000001-break-delete", timeout.Token));
                CheckResponse(await ExecuteAsync("2000000002-target-detach", timeout.Token));
                // extended-remote detach 保留 TCP；退出 GDB 后 OpenOCD 才触发 gdb-detach。
                CheckResponse(await ExecuteAsync("2000000003-gdb-exit", timeout.Token));
                await detached.Task.WaitAsync(timeout.Token);
                targetConnected = false;
            }
            catch (Exception ex) { detachFailure = ex; WriteLog("结束会话未能确认目标恢复运行：" + ex.Message); }
        }
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        closing = true;
        foreach (var (_, item) in pending) item.TrySetException(new StudioXException("DEBUG_DISCONNECTED", "调试会话已关闭。"));
        // 正常断开由应用层发送 detach；异常时只回收本会话拥有的进程，不附加复位/烧录动作。
        try
        {
            foreach (var process in new[] { gdb, openocd })
                if (process is not null)
                    try { if (!process.HasExited) process.Kill(true); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception ex) { WriteLog("进程回收失败，交由作业对象清理：" + ex.Message); }
        }
        finally
        {
            job.Dispose();
            try { await Task.WhenAll(readers); }
            finally { gdb?.Dispose(); openocd?.Dispose(); try { lock (logSync) log.Dispose(); } finally { lease.Dispose(); } }
        }
        if (detachFailure is not null) throw new StudioXException("DEBUG_DETACH", "调试进程和探针已释放，但未确认芯片恢复运行。请查看会话日志后重新连接。", detachFailure);
    }
    private static void CheckResponse(string text)
    {
        var record = MiRecord.Parse(text);
        if (record.Class == "error") throw new StudioXException("GDB_COMMAND", record.Data.String("msg"));
    }
}
