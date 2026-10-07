namespace StudioX.Desktop;

using System.Windows.Controls;
using StudioX.Application.Plugins;

public partial class MainWindow
{
    private readonly Dictionary<(string Plugin, string Adapter), (TabItem Tab, PluginDebugView View, PluginDebugViewSession Session, Action Changed)> pluginDebugViews = [];

    private Task OpenPluginDebugViewAsync(string pluginId, string adapterId)
    {
        if (pluginDebugViews.TryGetValue((pluginId, adapterId), out var existing))
        {
            ShowDocument(existing.Tab);
            return Task.CompletedTask;
        }
        if (pluginWorkspace is not { } workspace || !workspace.IsPluginRunning(pluginId))
        {
            return Task.CompletedTask;
        }
        var plugin = workspace.Contributions.Single(p => p.Id == pluginId);
        var adapter = plugin.Contribution.DebugAdapters.Single(a => a.Id == adapterId);
        var session = new PluginDebugViewSession(workspace, services.Debugger, pluginId, adapterId);
        var view = new PluginDebugView(adapter.DisplayName, plugin.Manifest.DisplayName + " · " + plugin.Manifest.Version, PluginManager.Log)
        {
            RefreshRequested = session.Refresh
        };
        var tab = AddToolTab("调试扩展 · " + adapter.DisplayName, view);
        Action changed = () => Dispatcher.BeginInvoke(() =>
        {
            if (!closing && !closed && pluginDebugViews.TryGetValue((pluginId, adapterId), out var current) && ReferenceEquals(current.Session, session))
            {
                // 取最新状态，不能将已排队的旧暂停面板重新显示到运行中的视图。
                view.Update(session.Current);
            }
        });
        pluginDebugViews.Add((pluginId, adapterId), (tab, view, session, changed));
        session.Changed += changed;
        view.Update(session.Current);
        ShowDocument(tab);
        return Task.CompletedTask;
    }

    private async Task<bool> ClosePluginDebugViewAsync(TabItem tab)
    {
        foreach (var (key, item) in pluginDebugViews.ToArray())
        {
            if (!ReferenceEquals(item.Tab, tab))
            {
                continue;
            }
            pluginDebugViews.Remove(key);
            item.Session.Changed -= item.Changed;
            item.View.RefreshRequested = null;
            await item.Session.DisposeAsync();
            WorkspaceTabs.Items.Remove(tab);
            return true;
        }
        return false;
    }

    private async Task ClearPluginDebugViewsAsync()
    {
        foreach (var item in pluginDebugViews.Values.ToArray())
        {
            await ClosePluginDebugViewAsync(item.Tab);
        }
    }

    private void RefreshPluginContributionActions() => PluginManager.SetContributions(ActivePluginContributions,
        id => FindPluginSession(id) is not null && !pluginStopped.Contains(id));
}
