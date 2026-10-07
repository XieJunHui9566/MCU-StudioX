namespace StudioX.Application.Plugins;

using System.Text.Json;
using System.Threading.Channels;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

/// <summary>订阅已有调试会话，合并刷新并撤销过期结果；不连接设备，也不控制目标。</summary>
public sealed class PluginDebugViewSession : IAsyncDisposable
{
    private readonly PluginWorkspaceSession workspace;
    private readonly DebugSessionService debugger;
    private readonly string pluginId, adapterId;
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<long> requests = Channel.CreateBounded<long>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest
    });
    private readonly Task worker;
    private CancellationTokenSource? reading;
    private DebugSnapshot? lastSnapshot;
    private string? lastReason, lastProject;
    private bool available = true, disposed;
    private PluginDebugViewUpdate current = new(0, DebugState.Disconnected, false, "等待调试快照");

    public PluginDebugViewSession(PluginWorkspaceSession workspace, DebugSessionService debugger, string pluginId, string adapterId)
    {
        if (workspace.IsApplicationSession || !workspace.IsPluginRunning(pluginId) || !workspace.Contributions.Any(p => p.Id == pluginId && p.Contribution.DebugAdapters.Any(a => a.Id == adapterId)))
        {
            throw new StudioXException("PLUGIN_DEBUG", "插件未运行或未声明该调试视图。");
        }
        this.workspace = workspace;
        this.debugger = debugger;
        this.pluginId = pluginId;
        this.adapterId = adapterId;
        worker = Task.Run(ReadAsync);
        debugger.Changed += DebuggerChanged;
        workspace.Changed += WorkspaceChanged;
        Refresh();
    }

    public event Action? Changed;
    public PluginDebugViewUpdate Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    public void Refresh() => QueueRefresh(force: true);
    private void DebuggerChanged() => QueueRefresh(force: false);

    private void QueueRefresh(bool force)
    {
        lock (gate)
        {
            if (disposed || !available)
            {
                return;
            }
            var matches = string.Equals(debugger.ProjectDirectory, workspace.Project, StringComparison.OrdinalIgnoreCase);
            var state = matches ? debugger.State : DebugState.Disconnected;
            if (!force && current.State == state && ReferenceEquals(lastSnapshot, debugger.Snapshot) &&
                lastReason == debugger.Reason && lastProject == debugger.ProjectDirectory)
            {
                return;
            }
            lastSnapshot = debugger.Snapshot;
            lastReason = debugger.Reason;
            lastProject = debugger.ProjectDirectory;
            reading?.Cancel();
            current = new(current.Revision + 1, state, matches && debugger.IsActive && debugger.IsHardware, state switch
            {
                DebugState.Stopped => "正在解释暂停快照…",
                DebugState.Running => "目标运行中，暂停后自动刷新。",
                DebugState.Starting => "调试正在启动，等待暂停快照。",
                DebugState.Stopping => "调试正在结束，已清除快照。",
                DebugState.Faulted => "调试失败，已清除快照；请查看调试输出。",
                _ => "未连接调试目标。启动并暂停已有调试会话后自动显示。"
            });
            if (state == DebugState.Stopped)
            {
                requests.Writer.TryWrite(current.Revision);
            }
        }
        Changed?.Invoke();
    }

    private void WorkspaceChanged(object? sender, PluginWorkspaceEvent update)
    {
        if (update.PluginId != pluginId || update.Kind is not ("stopped" or "crashed"))
        {
            return;
        }
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            available = false;
            reading?.Cancel();
            current = new(current.Revision + 1, DebugState.Disconnected, false, "插件已停止，当前视图已失效。");
        }
        Changed?.Invoke();
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await requests.Reader.WaitToReadAsync(lifetime.Token).ConfigureAwait(false))
            {
                if (!requests.Reader.TryRead(out var revision))
                {
                    continue;
                }
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                lock (gate)
                {
                    if (disposed || !available || current.Revision != revision)
                    {
                        continue;
                    }
                    reading = cancellation;
                }
                try
                {
                    // 单一后台读取循环与有界队列合并连续单步，不把插件延迟放进调试命令回调。
                    await Task.Delay(80, cancellation.Token).ConfigureAwait(false);
                    var input = await debugger.CapturePluginSnapshotAsync(workspace.Project!, revision, cancellation.Token).ConfigureAwait(false);
                    if (input.State != nameof(DebugState.Stopped))
                    {
                        continue;
                    }
                    var panel = await workspace.AdaptDebugSnapshotAsync(pluginId, adapterId,
                        JsonSerializer.SerializeToElement(input, JsonStore.Options), cancellation.Token).ConfigureAwait(false);
                    Complete(revision, panel, null);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception error) { Complete(revision, null, error.ToString()); }
                finally
                {
                    lock (gate)
                    {
                        if (ReferenceEquals(reading, cancellation))
                        {
                            reading = null;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private void Complete(long revision, StudioX.Extensions.Abstractions.PluginPanelDefinition? panel, string? diagnostic)
    {
        lock (gate)
        {
            if (disposed || !available || current.Revision != revision || current.State != DebugState.Stopped)
            {
                return;
            }
            current = current with
            {
                Panel = panel,
                Diagnostic = diagnostic,
                Status = diagnostic is null
                ? "已刷新暂停快照 · " + (current.Hardware ? "实机" : "离线模拟，未连接芯片")
                : "扩展解释失败；可以重试，原始诊断见下方。"
            };
        }
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            current = new(current.Revision + 1, DebugState.Disconnected, false, "调试扩展视图已关闭。");
            reading?.Cancel();
            lifetime.Cancel();
            requests.Writer.TryComplete();
        }
        debugger.Changed -= DebuggerChanged;
        workspace.Changed -= WorkspaceChanged;
        await worker.ConfigureAwait(false);
        lifetime.Dispose();
    }
}
