namespace StudioX.Application.Plugins;

using System.Text.Json;
using System.Text.Json.Serialization;
using StudioX.Extensions;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

/// <summary>拥有一个工作区内的插件宿主；停用先撤销回调资格，再终止子进程。</summary>
public sealed class PluginWorkspaceSession : IAsyncDisposable
{
    private static readonly JsonSerializerOptions PanelOptions = new(JsonStore.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string hostExecutable;
    private readonly Func<string, string, JsonElement, CancellationToken, Task<JsonElement>> broker;
    private readonly object gate = new();
    private readonly Dictionary<string, ActivePlugin> plugins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginPanelDefinition> panels = new(StringComparer.Ordinal);
    private readonly Queue<string> diagnostics = [];
    private PluginActiveContribution[] contributions = [];
    private bool disposed;

    internal PluginWorkspaceSession(string project, string hostExecutable,
        Func<string, string, JsonElement, CancellationToken, Task<JsonElement>> broker)
    {
        Project = project;
        this.hostExecutable = hostExecutable;
        this.broker = broker;
    }

    public string Project { get; }

    public IReadOnlyList<PluginActiveContribution> Contributions
    {
        get
        {
            lock (gate)
            {
                return contributions.ToArray();
            }
        }
    }

    public IReadOnlyDictionary<string, PluginPanelDefinition> LatestPanels
    {
        get
        {
            lock (gate)
            {
                return new Dictionary<string, PluginPanelDefinition>(panels, StringComparer.Ordinal);
            }
        }
    }

    public IReadOnlyList<string> Diagnostics
    {
        get
        {
            lock (gate)
            {
                return diagnostics.ToArray();
            }
        }
    }

    internal bool IsDisposed
    {
        get
        {
            lock (gate)
            {
                return disposed;
            }
        }
    }

    public event EventHandler<PluginWorkspaceEvent>? Changed;

    public bool IsPluginRunning(string pluginId)
    {
        lock (gate)
        {
            return !disposed && plugins.TryGetValue(pluginId, out var active) && active.Running &&
                active.Client?.IsRunning == true;
        }
    }

    internal bool MatchesCatalogEntry(PluginCatalogEntry entry)
    {
        lock (gate)
        {
            return !plugins.TryGetValue(entry.Id, out var active) || !active.Running ||
                (string.Equals(active.ManifestPath, entry.ManifestPath, StringComparison.OrdinalIgnoreCase) &&
                    active.Fingerprint == entry.ContentFingerprint);
        }
    }

    internal async Task StartAsync(IEnumerable<PluginCatalogEntry> catalog, CancellationToken cancellationToken)
    {
        var successful = new List<PluginActiveContribution>();
        foreach (var entry in catalog)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = entry.Manifest!;
            var active = new ActivePlugin(manifest, entry.ManifestPath, entry.ContentFingerprint);
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                plugins.Add(entry.Id, active);
            }
            try
            {
                active.Client = await PluginRuntimeClient.StartAsync(hostExecutable, entry.ManifestPath,
                    (tool, arguments, token) => CallHostAsync(active, tool, arguments, token),
                    (update, token) => OnRuntimeEventAsync(active, update, token),
                    contribution =>
                    {
                        PluginContributionValidator.Validate(manifest, contribution);
                        lock (gate)
                        {
                            active.Contribution = contribution;
                            foreach (var panel in contribution.Panels)
                            {
                                panels[PanelKey(entry.Id, panel.Id)] = panel;
                            }
                        }
                    }, cancellationToken).ConfigureAwait(false);
                successful.Add(new(entry.Id, manifest, active.Contribution!));
                _ = ObserveExitAsync(active);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                AddDiagnostic(entry.Id, ex.ToString());
                await StopPluginAsync(entry.Id).ConfigureAwait(false);
            }
        }
        lock (gate)
        {
            // Agent 描述在一次会话启动后固定；停用只撤销调用资格，不动态修改工具描述。
            contributions = successful.ToArray();
        }
    }

    public async Task<JsonElement> InvokeAsync(string pluginId, string kind, string id, JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        PluginContributionValidator.Identifier(id);
        PluginContributionValidator.ValidateJson(arguments);
        ActivePlugin active;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!plugins.TryGetValue(pluginId, out active!) || !active.Running || active.Client is null ||
                active.Contribution is null)
            {
                throw new StudioXException("PLUGIN_INACTIVE", "插件已停止或未成功激活：" + pluginId);
            }
            var declared = kind switch
            {
                "command" => active.Contribution.Commands.Any(command => command.Id == id),
                "agentTool" => active.Contribution.AgentTools.Any(tool => tool.Id == id),
                _ => throw new StudioXException("PLUGIN_INVOKE_KIND", "未知插件调用类型：" + kind)
            };
            if (!declared)
            {
                throw new StudioXException("PLUGIN_INVOKE_ID", "插件未注册该贡献：" + id);
            }
            if (kind == "agentTool")
            {
                PluginInputSchema.ValidateArguments(active.Contribution.AgentTools.Single(tool => tool.Id == id).InputSchema,
                    arguments);
            }
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, active.Lifetime.Token);
        try
        {
            var result = await active.Client.InvokeAsync(kind, id, arguments, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            PluginContributionValidator.ValidateJson(result);
            return result.Clone();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddDiagnostic(pluginId, ex.ToString());
            throw;
        }
    }

    internal async Task StopPluginAsync(string id)
    {
        ActivePlugin? active;
        lock (gate)
        {
            if (!plugins.TryGetValue(id, out active) || !active.Running)
            {
                return;
            }
            active.Running = false;
            foreach (var key in panels.Keys.Where(key => key.StartsWith(id + "/", StringComparison.Ordinal)).ToArray())
            {
                panels.Remove(key);
            }
        }
        active.Lifetime.Cancel();
        if (active.Client is not null)
        {
            try
            {
                await active.Client.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 一个崩溃宿主不能阻止其余工作区资源清理；原始退出诊断继续对外可见。
                AddDiagnostic(id, ex.ToString());
            }
            if (!string.IsNullOrWhiteSpace(active.Client.Diagnostics))
            {
                AddDiagnostic(id, active.Client.Diagnostics);
            }
        }
        Raise(new(id, "stopped", JsonSerializer.SerializeToElement(new { reason = "插件会话已停止" }, JsonStore.Options)));
    }

    public async ValueTask DisposeAsync()
    {
        string[] ids;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            ids = plugins.Keys.ToArray();
        }
        foreach (var id in ids)
        {
            await StopPluginAsync(id).ConfigureAwait(false);
        }
        lock (gate)
        {
            foreach (var active in plugins.Values)
            {
                active.Lifetime.Dispose();
            }
            panels.Clear();
            Changed = null;
        }
    }

    private async Task<JsonElement> CallHostAsync(ActivePlugin active, string tool, JsonElement arguments,
        CancellationToken cancellationToken)
    {
        EnsureRunning(active);
        if (string.IsNullOrWhiteSpace(tool) || tool.StartsWith("plugin_", StringComparison.OrdinalIgnoreCase) ||
            !(active.Manifest.HostTools ?? []).Contains(tool, StringComparer.Ordinal))
        {
            throw new StudioXException("PLUGIN_HOST_TOOL", "插件没有声明该宿主工具或请求了递归插件调用：" + tool);
        }
        PluginContributionValidator.ValidateJson(arguments);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, active.Lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        var result = await broker(active.Manifest.Id, tool, arguments.Clone(), linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        EnsureRunning(active);
        PluginContributionValidator.ValidateJson(result);
        return result.Clone();
    }

    private Task OnRuntimeEventAsync(ActivePlugin active, PluginRuntimeEvent update, CancellationToken cancellationToken)
    {
        try
        {
            return ProcessRuntimeEventAsync(active, update, cancellationToken);
        }
        catch (Exception ex)
        {
            AddDiagnostic(active.Manifest.Id, ex.ToString());
            throw;
        }
    }

    private Task ProcessRuntimeEventAsync(ActivePlugin active, PluginRuntimeEvent update, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (disposed || !active.Running || cancellationToken.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }
        }
        PluginContributionValidator.ValidateJson(update.Payload);
        if (update.Kind == "panel")
        {
            var panel = update.Payload.Deserialize<PluginPanelDefinition>(PanelOptions)
                ?? throw new StudioXException("PLUGIN_PANEL", "插件面板更新为空。");
            var contribution = active.Contribution
                ?? throw new StudioXException("PLUGIN_PANEL", "插件尚未完成贡献声明。");
            if (!contribution.Panels.Any(definition => definition.Id == panel.Id))
            {
                throw new StudioXException("PLUGIN_PANEL", "插件不能发布未声明的面板：" + panel.Id);
            }
            PluginContributionValidator.ValidatePanel(panel,
                contribution.Commands.Select(command => command.Id).ToHashSet(StringComparer.Ordinal));
            lock (gate)
            {
                if (disposed || !active.Running)
                {
                    return Task.CompletedTask;
                }
                panels[PanelKey(active.Manifest.Id, panel.Id)] = panel;
            }
        }
        else if (update.Kind is "log" or "error" or "exit")
        {
            AddDiagnostic(active.Manifest.Id, update.Payload.GetRawText());
        }
        else
        {
            throw new StudioXException("PLUGIN_EVENT", "未知插件事件：" + update.Kind);
        }
        lock (gate)
        {
            if (disposed || !active.Running)
            {
                return Task.CompletedTask;
            }
            Raise(new(active.Manifest.Id, update.Kind, update.Payload.Clone()));
        }
        return Task.CompletedTask;
    }

    private void EnsureRunning(ActivePlugin active)
    {
        lock (gate)
        {
            if (disposed || !active.Running || active.Lifetime.IsCancellationRequested)
            {
                throw new OperationCanceledException("插件工作区已撤销调用资格。", active.Lifetime.Token);
            }
        }
    }

    private async Task ObserveExitAsync(ActivePlugin active)
    {
        try
        {
            await active.Client!.Completion.ConfigureAwait(false);
            lock (gate)
            {
                if (!active.Running || disposed)
                {
                    return;
                }
            }
            AddDiagnostic(active.Manifest.Id, "插件协议连接意外结束。");
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                if (!active.Running || disposed)
                {
                    return;
                }
            }
            AddDiagnostic(active.Manifest.Id, ex.ToString());
        }
        await StopPluginAsync(active.Manifest.Id).ConfigureAwait(false);
    }

    private void AddDiagnostic(string id, string message)
    {
        lock (gate)
        {
            diagnostics.Enqueue(id + ": " + message);
            while (diagnostics.Count > 512)
            {
                diagnostics.Dequeue();
            }
        }
    }

    private void Raise(PluginWorkspaceEvent update)
    {
        try
        {
            Changed?.Invoke(this, update);
        }
        catch (Exception ex)
        {
            AddDiagnostic(update.PluginId, "插件事件观察者失败：" + ex);
        }
    }

    private static string PanelKey(string pluginId, string panelId) => pluginId + "/" + panelId;

    private sealed class ActivePlugin(PluginManifest manifest, string manifestPath, string? fingerprint)
    {
        public PluginManifest Manifest { get; } = manifest;
        public string ManifestPath { get; } = manifestPath;
        public string? Fingerprint { get; } = fingerprint;
        public CancellationTokenSource Lifetime { get; } = new();
        public bool Running { get; set; } = true;
        public PluginRuntimeClient? Client { get; set; }
        public PluginContribution? Contribution { get; set; }
    }
}
