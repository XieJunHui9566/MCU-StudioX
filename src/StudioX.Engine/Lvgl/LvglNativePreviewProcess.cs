namespace StudioX.Engine.Lvgl;

using System.Diagnostics;
using System.Text;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

/// <summary>只管理独立 PC 窗口；Job 确保 IDE 退出后不残留预览进程。</summary>
public sealed class LvglNativePreviewProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly SemaphoreSlim inputGate = new(1, 1);
    private DebugProcessJob? job;
    private Task completion = Task.CompletedTask;
    private bool started;
    public event Action<string>? LineReceived;
    public event Action<int>? Exited;
    public bool IsRunning => started && !process.HasExited;

    public LvglNativePreviewProcess(string executable, string workingDirectory)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
        {
            throw new StudioXException("LVGL_EXECUTABLE", "PC 预览可执行文件不存在。");
        }
        process = new()
        {
            StartInfo = new(executable)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
    }

    public void Start()
    {
        if (started)
        {
            throw new InvalidOperationException("预览进程已启动。");
        }
        job = new DebugProcessJob();
        try
        {
            if (!process.Start())
            {
                throw new StudioXException("LVGL_PROCESS_START", "PC 预览进程无法启动。");
            }
            started = true;
            job.Add(process);
            completion = ObserveAsync();
        }
        catch { job.Dispose(); job = null; throw; }
    }

    public async Task SendAsync(string json, CancellationToken token = default)
    {
        if (json.Length > 16_384 || json.Contains('\n') || json.Contains('\r'))
        {
            throw new StudioXException("LVGL_COMMAND", "预览命令无效。");
        }
        await inputGate.WaitAsync(token);
        try
        {
            if (!IsRunning)
            {
                throw new StudioXException("LVGL_NOT_RUNNING", "PC 预览当前未运行。");
            }
            await process.StandardInput.WriteLineAsync(json.AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
        }
        finally { inputGate.Release(); }
    }

    public async Task StopAsync(CancellationToken token = default)
    {
        if (!started || process.HasExited)
        {
            return;
        }
        try
        {
            await SendAsync("{\"command\":\"stop\"}", token);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or StudioXException)
        {
            LineReceived?.Invoke("停止命令发送失败：" + ex.Message);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(CancellationToken.None);
        }
        await completion;
    }

    private async Task ObserveAsync()
    {
        await Task.WhenAll(DrainAsync(process.StandardOutput), DrainAsync(process.StandardError), process.WaitForExitAsync());
        Exited?.Invoke(process.ExitCode);
    }

    private async Task DrainAsync(StreamReader reader)
    {
        var line = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                var character = buffer[i];
                if (character == '\n')
                {
                    if (truncated)
                    {
                        LineReceived?.Invoke("[宿主单行输出超过 64 KiB，已截断]");
                    }
                    else if (line.Length > 0)
                    {
                        LineReceived?.Invoke(line.ToString().TrimEnd('\r'));
                    }
                    line.Clear();
                    truncated = false;
                }
                else if (line.Length < 64 * 1024)
                {
                    line.Append(character);
                }
                else
                {
                    truncated = true;
                }
            }
        }
        if (line.Length > 0)
        {
            LineReceived?.Invoke(truncated ? "[宿主末行输出超过 64 KiB，已截断]" : line.ToString());
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        finally { job?.Dispose(); process.Dispose(); inputGate.Dispose(); }
    }
}
