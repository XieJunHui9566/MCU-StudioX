namespace StudioX.Desktop;

using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using StudioX.Application.Plugins;
using StudioX.Foundation;

public partial class MainWindow
{
    private readonly Dictionary<string, object> pendingDocumentEvents = new(StringComparer.OrdinalIgnoreCase);
    private bool publishingDocumentEvents;
    private async Task InitializePluginProductivityAsync(PluginWorkspaceSession workspace, CancellationToken token)
    {
        foreach (var plugin in workspace.Contributions)
        {
            if (plugin.Contribution.Settings.Length > 0)
            {
                var values = await services.PluginSettings.ReadAsync(plugin.Id, plugin.Contribution.Settings, token);
                if (plugin.Contribution.Events.Contains("settings.changed"))
                {
                    await workspace.InvokeAsync(plugin.Id, "event", "settings.changed", JsonSerializer.SerializeToElement(values, JsonStore.Options), token);
                }
            }
        }
        await workspace.PublishWorkspaceEventAsync("project.opened", new
        {
            project = Path.GetFileName(workspace.Project),
            apiVersion = 3
        }, token);
        foreach (var document in editorDocuments)
        {
            QueuePluginDocumentEvent("document.opened", document);
        }
    }
    private void QueuePluginDocumentEvent(string kind, EditorDocumentSession document)
    {
        if (pluginWorkspace is null || closing || closed)
        {
            return;
        }
        // 合并连续输入，事件只携带元信息；正文通过已有授权编辑器接口读取。
        pendingDocumentEvents[kind + "/" + document.Source.RelativePath] = new
        {
            kind,
            path = document.Source.RelativePath,
            dirty = document.IsDirty,
            length = document.Buffer.TextLength
        };
    }
    private async void FlushPluginDocumentEvents()
    {
        if (publishingDocumentEvents || pluginWorkspace is not { } workspace || pendingDocumentEvents.Count == 0)
        {
            return;
        }
        publishingDocumentEvents = true;
        var batch = pendingDocumentEvents.Take(64).ToArray();
        foreach (var entry in batch)
        {
            pendingDocumentEvents.Remove(entry.Key);
        }
        var events = batch.Select(entry => entry.Value).ToArray();
        try
        {
            foreach (var payload in events)
            {
                var json = JsonSerializer.SerializeToElement(payload, JsonStore.Options);
                await workspace.PublishWorkspaceEventAsync(json.GetProperty("kind").GetString()!, payload, pluginWorkspaceCancellation?.Token ?? default);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PluginManager.Log(ex.ToString()); }
        finally { publishingDocumentEvents = false; }
    }
    private async Task ShowPluginSettingsAsync()
    {
        if (pluginWorkspace is not { } workspace)
        {
            return;
        }
        var choices = workspace.Contributions.Where(p => p.Contribution.Settings.Length > 0).ToArray();
        var picker = new QuickPickWindow("插件设置", (q, _) => Task.FromResult<IReadOnlyList<QuickPickItem>>(choices.Where(p => p.Manifest.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)).Select(p => new QuickPickItem(p.Manifest.DisplayName, p.Id, p)).ToArray())) { Owner = this };
        if (picker.ShowDialog() != true || picker.Selected?.Value is not PluginActiveContribution plugin)
        {
            return;
        }
        await RunAsync(async token =>
        {
            var values = await services.PluginSettings.ReadAsync(plugin.Id, plugin.Contribution.Settings, token);
            var dialog = new PluginSettingsWindow(plugin.Manifest.DisplayName + " · 设置", plugin.Contribution.Settings, values) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Values is null)
            {
                return;
            }
            await services.PluginSettings.SaveAsync(plugin.Id, plugin.Contribution.Settings, dialog.Values, token);
            if (workspace == pluginWorkspace && plugin.Contribution.Events.Contains("settings.changed"))
            {
                await workspace.InvokeAsync(plugin.Id, "event", "settings.changed", JsonSerializer.SerializeToElement(dialog.Values, JsonStore.Options), token);
            }
            Status.Text = "插件设置已保存。";
        });
    }
    private async Task ShowPluginDebugAdaptersAsync()
    {
        if (pluginWorkspace is not { } workspace)
        {
            return;
        }
        var items = workspace.Contributions.SelectMany(p => p.Contribution.DebugAdapters.Select(a => new QuickPickItem(a.DisplayName, p.Id, (p.Id, a.Id)))).ToArray();
        var picker = new QuickPickWindow("调试快照扩展", (q, _) => Task.FromResult<IReadOnlyList<QuickPickItem>>(items.Where(i => i.ToString().Contains(q, StringComparison.OrdinalIgnoreCase)).ToArray())) { Owner = this };
        if (picker.ShowDialog() != true || picker.Selected?.Value is not ValueTuple<string, string> selected)
        {
            return;
        }
        await RunAsync(async token =>
        {
            var snapshot = JsonSerializer.SerializeToElement(new
            {
                state = services.Debugger.State,
                hardware = services.Debugger.IsHardware,
                snapshot = services.Debugger.Snapshot
            }, JsonStore.Options);
            var panel = await workspace.AdaptDebugSnapshotAsync(selected.Item1, selected.Item2, snapshot, token);
            var renderer = new PluginPanelRenderer((_, _) => Task.CompletedTask, PluginManager.Log);
            var tab = AddToolTab("调试快照 · " + panel.Title, new ScrollViewer { Content = renderer.Render(panel), VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            ShowDocument(tab);
        });
    }
}
