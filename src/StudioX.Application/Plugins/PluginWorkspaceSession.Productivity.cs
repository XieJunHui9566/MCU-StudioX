namespace StudioX.Application.Plugins;

using System.Text.Json;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

public sealed partial class PluginWorkspaceSession
{
    public bool SupportsLanguage(string path) => Contributions.Any(p => IsPluginRunning(p.Id) && p.Contribution.Languages.Any(l => l.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)));
    public async Task PublishWorkspaceEventAsync(string kind, object payload, CancellationToken token = default)
    {
        var json = JsonSerializer.SerializeToElement(payload, JsonStore.Options);
        foreach (var plugin in Contributions.Where(p => p.Contribution.Events.Contains(kind, StringComparer.Ordinal)))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await InvokeAsync(plugin.Id, "event", kind, json, deadline.Token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested) { AddDiagnostic(plugin.Id, ex.ToString()); }
        }
    }
    public async Task<IReadOnlyList<PluginCompletion>> CompleteAsync(PluginLanguageRequest request, CancellationToken token = default)
    {
        if (request.Text.Length > 256 * 1024 || request.Offset < 0 || request.Offset > request.Text.Length)
        {
            return [];
        }
        var results = new List<PluginCompletion>();
        foreach (var plugin in Contributions)
        {
            foreach (var language in plugin.Contribution.Languages.Where(l => l.Extensions.Contains(Path.GetExtension(request.Path), StringComparer.OrdinalIgnoreCase)))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    var response = await InvokeAsync(plugin.Id, "language", language.Id, JsonSerializer.SerializeToElement(request, JsonStore.Options), deadline.Token);
                    var items = response.Deserialize<PluginCompletion[]>(JsonStore.Options) ?? [];
                    if (items.Length > 256 || items.Any(i => i is null || string.IsNullOrWhiteSpace(i.Label) || i.Label.Length > 256 || i.InsertText is null || i.InsertText.Length > 4096 || i.Detail is null || i.Detail.Length > 4096))
                    {
                        throw new StudioXException("PLUGIN_LANGUAGE", "语言扩展补全响应无效。");
                    }
                    results.AddRange(items);
                }
                catch (Exception ex) when (!token.IsCancellationRequested) { AddDiagnostic(plugin.Id, ex.ToString()); }
            }
        }
        return results.Take(512).ToArray();
    }
    public async Task<PluginPanelDefinition> AdaptDebugSnapshotAsync(string pluginId, string adapterId, JsonElement snapshot, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var response = await InvokeAsync(pluginId, "debugAdapter", adapterId, snapshot, deadline.Token);
        var panel = response.Deserialize<PluginPanelDefinition>(PanelOptions) ?? throw new StudioXException("PLUGIN_DEBUG", "调试适配器返回为空。");
        PluginContributionValidator.ValidatePanel(panel, new HashSet<string>());
        return panel;
    }
}
