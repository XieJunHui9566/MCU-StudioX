namespace StudioX.Desktop;

using System.Text.Json;
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
    private async Task ShowPluginSettingsAsync(string? pluginId = null)
    {
        if (pluginWorkspace is not { } workspace)
        {
            return;
        }
        var choices = workspace.Contributions.Where(p => workspace.IsPluginRunning(p.Id) && p.Contribution.Settings.Length > 0 && (pluginId is null || p.Id == pluginId)).ToArray();
        if (choices.Length == 0)
        {
            Status.Text = "当前工程没有可用的插件设置；请先打开工程并启用具有设置能力的插件。";
            return;
        }
        var plugin = choices[0];
        if (choices.Length > 1)
        {
            var picker = new QuickPickWindow("插件设置", (q, _) => Task.FromResult<IReadOnlyList<QuickPickItem>>(choices.Where(p => p.Manifest.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)).Select(p => new QuickPickItem(p.Manifest.DisplayName, p.Id, p)).ToArray())) { Owner = this };
            if (picker.ShowDialog() != true || picker.Selected?.Value is not PluginActiveContribution selected)
            {
                return;
            }
            plugin = selected;
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
            foreach (var item in pluginDebugViews.Where(p => p.Key.Plugin == plugin.Id).Select(p => p.Value))
            {
                item.Session.Refresh();
            }
            Status.Text = "插件设置已保存。";
        });
    }
    private async Task ShowPluginDebugAdaptersAsync(string? pluginId = null)
    {
        if (pluginWorkspace is not { } workspace)
        {
            Status.Text = "请先打开工程，启用具有调试快照能力的插件。";
            return;
        }
        var items = workspace.Contributions.Where(p => workspace.IsPluginRunning(p.Id) && (pluginId is null || p.Id == pluginId))
            .SelectMany(p => p.Contribution.DebugAdapters.Select(a => new QuickPickItem(a.DisplayName, p.Manifest.DisplayName + " · " + p.Id, (p.Id, a.Id)))).ToArray();
        if (items.Length == 0)
        {
            Status.Text = "当前没有运行中的调试快照扩展；可在插件管理页按“调试快照”筛选。";
            ShowDocument(ExtensionsTab);
            return;
        }
        var selected = (ValueTuple<string, string>)items[0].Value!;
        if (items.Length > 1)
        {
            var picker = new QuickPickWindow("调试快照扩展", (q, _) => Task.FromResult<IReadOnlyList<QuickPickItem>>(items.Where(i => i.ToString().Contains(q, StringComparison.OrdinalIgnoreCase)).ToArray())) { Owner = this };
            if (picker.ShowDialog() != true || picker.Selected?.Value is not ValueTuple<string, string> choice)
            {
                return;
            }
            selected = choice;
        }
        await OpenPluginDebugViewAsync(selected.Item1, selected.Item2);
    }
}
