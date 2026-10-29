namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Extensions.Abstractions;

public partial class MainWindow
{
    private readonly Dictionary<string, (PluginToolWindow Window, Button Launcher)> pluginWindows = new(StringComparer.Ordinal);

    private void PlacePluginPanel(string key, string pluginId, PluginPanelDefinition panel, ContentControl host)
    {
        var body = pluginActivities.TryGetValue(pluginId, out var activity) ? activity.Body : PluginManager.PanelsHost;
        if (panel.Presentation == "window")
        {
            if (pluginWindows.TryGetValue(key, out var current))
            {
                current.Window.Title = panel.Title + " · MCU StudioX";
                return;
            }
            if (host.Parent is Panel previous)
            {
                previous.Children.Remove(host);
            }
            var window = new PluginToolWindow(this, panel.Title, host);
            var launcher = new Button { Content = "在独立窗口打开：" + panel.Title, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 8) };
            launcher.Click += (_, _) => { if (CanRunPlugin(pluginId)) { window.ShowOrActivate(); } };
            body.Children.Add(launcher);
            pluginWindows.Add(key, (window, launcher));
        }
        else
        {
            ClosePluginWindow(key);
            if (host.Parent is null)
            {
                body.Children.Add(host);
            }
        }
    }

    private void ShowPluginActivity(string pluginId, TabItem tab)
    {
        var windows = pluginWindows.Where(pair => pair.Key.StartsWith(pluginId + "/", StringComparison.Ordinal)).Select(pair => pair.Value.Window).ToArray();
        if (windows.Length == 0)
        {
            ShowDocument(tab);
        }
        else if (CanRunPlugin(pluginId))
        {
            foreach (var window in windows)
            {
                window.ShowOrActivate();
            }
        }
    }

    private void ClosePluginWindow(string key)
    {
        if (!pluginWindows.Remove(key, out var surface))
        {
            return;
        }
        if (surface.Launcher.Parent is Panel parent)
        {
            parent.Children.Remove(surface.Launcher);
        }
        surface.Window.CloseSession();
    }

    private void ClosePluginWindows(string pluginId)
    {
        foreach (var key in pluginWindows.Keys.Where(key => key.StartsWith(pluginId + "/", StringComparison.Ordinal)).ToArray())
        {
            ClosePluginWindow(key);
        }
    }
}
