namespace StudioX.Application.Mcp;

using System.Text;
using System.Text.Json;
using StudioX.Foundation;

/// <summary>每个 MCP 会话只管理自身的长操作；取消请求不冒充进程已经退出。</summary>
internal sealed class McpOperationTracker : IAsyncDisposable
{
    private readonly object gate = new();
    private Operation? current;
    private bool disposed;

    internal Operation Start(string tool, TimeSpan timeout, CancellationToken token, string? logPath = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (current is { Finished: false })
            {
                throw new StudioXException("MCP_OPERATION_BUSY", "本会话已有编译或下载操作；请查询状态或取消后等待其结束。");
            }
            current = new Operation(tool, timeout, token, logPath);
            return current;
        }
    }

    internal string Status()
    {
        lock (gate)
        {
            return current?.Status() ?? JsonSerializer.Serialize(new
            {
                state = "idle",
                operationId = (string?)null
            });
        }
    }

    internal string ToolFor(string id)
    {
        lock (gate)
        {
            return RequireCurrent(id).Tool;
        }
    }

    internal string Cancel(string id)
    {
        lock (gate)
        {
            var operation = RequireCurrent(id);
            operation.Cancel();
            return operation.Status();
        }
    }

    private Operation RequireCurrent(string id) => current is { } operation && operation.Id == id
        ? operation : throw new StudioXException("MCP_OPERATION_ID", "操作标识不属于本会话的当前操作，请重新查询状态。");

    public async ValueTask DisposeAsync()
    {
        Operation? operation;
        lock (gate)
        {
            disposed = true;
            operation = current;
            operation?.Cancel();
        }
        if (operation is not null)
        {
            // 操作作用域在进程清理结束后释放；关闭会话必须等所有者完成，不能遗留后台下载。
            await operation.Completion.ConfigureAwait(false);
        }
    }

    internal sealed class Operation : IDisposable
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource timeout;
        private readonly CancellationTokenSource cancellation;
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly StringBuilder output = new();
        private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
        private DateTimeOffset? ended;
        private DateTimeOffset? lastOutput;
        private string phase = "准备操作";
        private string state = "running";
        private string? logPath;
        private bool outputTruncated;
        private int? exitCode;
        private bool expired;
        private bool canceled;

        internal Operation(string tool, TimeSpan limit, CancellationToken token, string? logPath)
        {
            Tool = tool;
            this.logPath = logPath;
            timeout = new CancellationTokenSource(limit);
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
            Progress = new Sink(SetPhase);
            Output = new Sink(AppendOutput);
        }

        internal string Id { get; } = Guid.NewGuid().ToString("N");
        internal string Tool
        {
            get;
        }
        internal CancellationToken Token => cancellation.Token;
        internal bool Finished
        {
            get
            {
                lock (gate)
                {
                    return ended is not null;
                }
            }
        }
        internal Task Completion => completion.Task;
        internal IProgress<string> Progress
        {
            get;
        }
        internal IProgress<string> Output
        {
            get;
        }

        private void SetPhase(string text)
        {
            lock (gate)
            {
                if (ended is null)
                {
                    phase = text.Length > 600 ? text[..600] : text;
                }
            }
        }

        private void AppendOutput(string text)
        {
            lock (gate)
            {
                if (ended is not null)
                {
                    return;
                }
                lastOutput = DateTimeOffset.UtcNow;
                const int maximum = 4096;
                if (text.Length >= maximum)
                {
                    output.Clear().Append(text.AsSpan(text.Length - maximum));
                    outputTruncated = true;
                }
                else
                {
                    output.Append(text);
                    if (output.Length > maximum)
                    {
                        output.Remove(0, output.Length - maximum);
                        outputTruncated = true;
                    }
                }
            }
        }

        internal void Complete(bool success, int? code, string? path, bool timedOut)
        {
            lock (gate)
            {
                state = timedOut ? "timed_out" : success ? "succeeded" : "failed";
                phase = timedOut ? "操作超时，工具已退出" : success ? "操作成功" : "操作失败";
                exitCode = code;
                logPath = path ?? logPath;
                expired = timedOut;
                ended = DateTimeOffset.UtcNow;
            }
        }

        internal Exception Interrupted(OperationCanceledException error)
        {
            lock (gate)
            {
                expired = timeout.IsCancellationRequested;
                canceled = !expired;
                state = expired ? "timed_out" : "canceled";
                phase = expired ? "操作超时，工具已停止" : "操作已取消，工具已停止";
                ended = DateTimeOffset.UtcNow;
                return expired ? new StudioXException("MCP_OPERATION_TIMEOUT", "操作超过会话时间上限，工具已停止；原始输出见操作状态与日志。", error) : error;
            }
        }

        internal void Cancel()
        {
            lock (gate)
            {
                if (ended is not null)
                {
                    return;
                }
                canceled = true;
                phase = "已请求取消，等待工具退出";
            }
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException) when (Finished) { /* 正常完成可与取消请求交错。 */ }
        }

        internal string Status()
        {
            lock (gate)
            {
                return JsonSerializer.Serialize(new
                {
                    operationId = Id,
                    tool = Tool,
                    state = ended is null && cancellation.IsCancellationRequested ? "stopping" : state,
                    phase,
                    startedAtUtc = started,
                    endedAtUtc = ended,
                    timestampSource = "host-utc",
                    elapsedSeconds = ((ended ?? DateTimeOffset.UtcNow) - started).TotalSeconds,
                    lastOutputAtUtc = lastOutput,
                    cancelRequested = canceled,
                    timedOut = expired || ended is null && timeout.IsCancellationRequested,
                    exitCode,
                    logPath,
                    recentOutput = output.ToString(),
                    outputTruncated,
                    message = ended is null ? "无输出不等于停滞；取消完成以终态为准。" : "操作已结束；完整诊断见日志。"
                });
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (ended is null)
                {
                    state = "failed";
                    phase = "操作失败，原始诊断见工具结果";
                    ended = DateTimeOffset.UtcNow;
                }
                timeout.Dispose();
                cancellation.Dispose();
            }
            completion.TrySetResult();
        }

        private sealed class Sink(Action<string> report) : IProgress<string>
        {
            public void Report(string value) => report(value);
        }
    }
}
