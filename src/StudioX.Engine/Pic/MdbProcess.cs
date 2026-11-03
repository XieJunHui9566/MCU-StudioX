namespace StudioX.Engine.Pic;

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

/// <summary>MDB 独占进程与有界命令通道；用唯一 echo 屏障区分命令结束和异步停止通知。</summary>
public sealed class MdbProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly DebugProcessJob job;
    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object disposalSync = new();
    private Task? disposal;
    private readonly object sync = new();
    private readonly StreamWriter log;
    private readonly StringBuilder buffer = new();
    private readonly StringBuilder stopBuffer = new();
    private TaskCompletionSource<string>? pending;
    private string? marker;
    private bool toolPromptBarrier, dialogHandled, programmingBarrier;
    private readonly string? verifiedDevice;
    private readonly Task stdout, stderr;
    private volatile bool disposed;
    public bool IsAlive => !disposed && !process.HasExited;
    public event Action<string>? Output;
    public event Action<string>? Stopped;
    public event Action<Exception>? Failed;

    public MdbProcess(ResolvedToolset tools, string userDirectory, string logPath, string? verifiedDevice = null)
    {
        this.verifiedDevice = verifiedDevice;
        Directory.CreateDirectory(userDirectory);
        var temporary = Path.Combine(userDirectory, "tmp");
        Directory.CreateDirectory(temporary);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        log = new(logPath, false, new UTF8Encoding(false))
        {
            AutoFlush = true
        };
        job = new();
        var start = new ProcessStartInfo(tools.Tool("java"))
        {
            WorkingDirectory = userDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var variable in ToolsetEnvironment.AmbientVariables.Concat(new[] { "JAVA_HOME", "JRE_HOME", "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "CLASSPATH" }))
        {
            start.Environment.Remove(variable);
        }
        foreach (var (name, value) in ToolsetEnvironment.Create(tools))
        {
            start.Environment[name] = value;
        }
        foreach (var argument in new[] { "-Duser.home=" + userDirectory, "-Djava.io.tmpdir=" + temporary, "-Duser.language=en", "-Duser.country=US", "-Dfile.encoding=UTF-8", "-jar", tools.Tool("mdb") })
        {
            start.ArgumentList.Add(argument);
        }
        process = new()
        {
            StartInfo = start
        };
        try
        {
            process.Start();
            job.Add(process);
            stdout = ReadAsync(process.StandardOutput, false);
            stderr = ReadAsync(process.StandardError, true);
        }
        catch { process.Dispose(); job.Dispose(); log.Dispose(); throw; }
    }

    public async Task<string> ExecuteAsync(string command, CancellationToken token = default, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 8192 || command.Any(c => c is '\r' or '\n' or '\0'))
        {
            throw new StudioXException("MDB_COMMAND", "MDB 命令格式无效。");
        }
        ObjectDisposedException.ThrowIf(disposed, this);
        // 截止时间从排队开始计算；管道写满时也必须能够超时并回收本会话进程。
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(45));
        var acquired = false;
        try
        {
            await commands.WaitAsync(deadline.Token);
            acquired = true;
            ObjectDisposedException.ThrowIf(disposed, this);
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            string sentinel;
            lock (sync)
            {
                buffer.Clear();
                sentinel = "STUDIOX_" + Guid.NewGuid().ToString("N");
                marker = "/*" + sentinel + "*/";
                pending = completion;
                programmingBarrier = command.StartsWith("program ", StringComparison.OrdinalIgnoreCase);
                toolPromptBarrier = command.StartsWith("hwtool ", StringComparison.OrdinalIgnoreCase) || programmingBarrier;
                dialogHandled = false;
                log.WriteLine("> " + command);
            }
            string result;
            try
            {
                await process.StandardInput.WriteLineAsync(command.AsMemory(), deadline.Token);
                if (!toolPromptBarrier)
                {
                    await process.StandardInput.WriteLineAsync(("echo " + sentinel).AsMemory(), deadline.Token);
                }
                await process.StandardInput.FlushAsync(deadline.Token);
                result = await completion.Task.WaitAsync(deadline.Token);
            }
            catch
            {
                // 命令取消后不复用进程，防止迟到响应落入下一条命令，或仍有编程操作在后台运行。
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
                throw;
            }
            if (Regex.IsMatch(result, @"(?im)(?:^|[\r\n>])\s*(?:Error\s*:|Underfined command|Undefined command|Exception|Failed\b)|Symbol does not exist|Program failed|Connection Failed"))
            {
                throw new StudioXException("MDB_COMMAND", result.Trim());
            }
            return result;
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("MDB 命令在排队、发送或等待应答时超时。", ex);
        }
        finally { if (acquired) { commands.Release(); } }
    }

    private async Task ReadAsync(StreamReader reader, bool error)
    {
        var chars = new char[2048];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(chars)) > 0)
            {
                var text = new string(chars, 0, count);
                string? stopped = null;
                string? reply = null;
                lock (sync)
                {
                    log.Write(text);
                    if (pending is not null)
                    {
                        buffer.Append(text);
                        if (!error && buffer.ToString().Contains("Would you like MPLAB to change the config setting(s) and continue?", StringComparison.Ordinal))
                        {
                            throw new StudioXException("PIC_DEBUG_CONFIG", "MDB 要求修改未确认的调试配置，已中止并保留原始诊断。请在源码调试配置中明确设置：\n" + buffer);
                        }
                        if (buffer.Length > 1024 * 1024)
                        {
                            throw new StudioXException("MDB_OUTPUT_LIMIT", "MDB 单条响应超过 1 MiB。");
                        }
                        if (marker is not null && buffer.ToString().IndexOf(marker, StringComparison.Ordinal) is var end && end >= 0)
                        {
                            pending.TrySetResult(buffer.ToString(0, end));
                            pending = null;
                            marker = null;
                            buffer.Clear();
                        }
                        else if (!error && toolPromptBarrier)
                        {
                            var response = buffer.ToString();
                            var identified = Regex.IsMatch(response, @"Target device \S+ found\.");
                            var question = Regex.Match(response, @"CAUTION: Check that the device selected in MPLAB IDE \(([^)]+)\)[\s\S]*?Do you wish to continue\?");
                            if (question.Success && !dialogHandled)
                            {
                                if (verifiedDevice is null || question.Groups[1].Value != verifiedDevice)
                                {
                                    throw new StudioXException("MDB_CONFIRMATION", "MDB 询问的器件未经相同序列号回读核对，未继续连接。\n" + question.Value);
                                }
                                dialogHandled = true;
                                if (!identified)
                                {
                                    reply = "yes";
                                    log.WriteLine("\n[StudioX：仅应答已回读核对的准确器件电压提示，不自动接受其他对话。]");
                                }
                            }
                            if (Regex.IsMatch(response, @"(?:^|[\r\n])>\s*\z") &&
                                ((!programmingBarrier && (identified || response.Contains("Resetting SFRs", StringComparison.Ordinal))) ||
                                 response.Contains("Program succeeded.", StringComparison.Ordinal) || response.Contains("Program failed", StringComparison.OrdinalIgnoreCase) ||
                                 Regex.IsMatch(response, @"(?im)^\s*>?Error\s*:")))
                            {
                                // 不能把 echo 预先排在工具选择之后，它会被厂商对话当作回答吞掉。
                                toolPromptBarrier = false;
                                reply = "echo " + marker![2..^2];
                            }
                        }
                    }
                    if (!error)
                    {
                        stopBuffer.Append(text);
                        var current = stopBuffer.ToString();
                        // MDB 的命令提示符可能插入异步暂停报告的字段之间。
                        var stop = Regex.Match(current, @"Stop at[\s>]+address:\s*(0x[0-9a-fA-F]+)[\s>]+file:([^\r\n]+)[\s>]+source line:(\d+)\s*[\r\n]");
                        if (stop.Success)
                        {
                            stopped = stop.Value;
                            stopBuffer.Remove(0, stop.Index + stop.Length);
                        }
                        if (stopBuffer.Length > 65536)
                        {
                            stopBuffer.Remove(0, stopBuffer.Length - 8192);
                        }
                    }
                }
                Output?.Invoke(text);
                if (reply is not null)
                {
                    await process.StandardInput.WriteLineAsync(reply.AsMemory(), lifetime.Token);
                    await process.StandardInput.FlushAsync(lifetime.Token);
                }
                if (stopped is not null)
                {
                    Stopped?.Invoke(stopped);
                }
            }
            if (!disposed && !error)
            {
                throw new StudioXException("MDB_EXIT", "MDB 进程已退出，请查看原始调试日志。");
            }
        }
        catch (Exception failure)
        {
            lock (sync)
            {
                pending?.TrySetException(failure);
            }
            if (!disposed)
            {
                Failed?.Invoke(failure);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalSync)
        {
            return new(disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        disposed = true;
        lifetime.Cancel();
        lock (sync)
        {
            pending?.TrySetException(new ObjectDisposedException(nameof(MdbProcess)));
        }
        var acquired = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            // 与命令写入互斥；五秒同时约束排队、quit 写入、刷新和退出，不能只约束最后一步。
            await commands.WaitAsync(deadline.Token);
            acquired = true;
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("quit".AsMemory(), deadline.Token);
                await process.StandardInput.FlushAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
        {
            lock (sync)
            {
                log.WriteLine(ex);
            }
        }
        finally
        {
            // 先关闭作业对象，连同仍持有输出管道的子进程一起回收，再等待读取任务结束。
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            finally
            {
                job.Dispose();
                if (acquired) { commands.Release(); }
            }
        }
        // 被取消的写入完成后才释放资源，避免命令 finally 访问已释放的信号量。
        await commands.WaitAsync();
        commands.Release();
        await Task.WhenAll(stdout, stderr);
        process.Dispose();
        log.Dispose();
        commands.Dispose();
        lifetime.Dispose();
    }
}
