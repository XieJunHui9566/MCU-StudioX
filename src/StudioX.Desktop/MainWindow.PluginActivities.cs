namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using StudioX.Application.Plugins;

public partial class MainWindow
{
    private readonly Dictionary<string, PluginActivity> pluginActivities = new(StringComparer.Ordinal);

    private void AddPluginActivity(PluginActiveContribution active)
    {
        if (pluginActivities.ContainsKey(active.Id) || active.Manifest.Activity is not { } definition)
        {
            return;
        }
        var title = definition.Title ?? active.Manifest.DisplayName;
        // 入口与图案完全来自插件清单；宿主没有插件 ID 或插件专属图案分支。
        FrameworkElement icon = definition.Icon is { } drawing
            ? new PluginActivityIconView(drawing) { Width = 18, Height = 18 }
            : new Icon { Kind = "plug", Width = 18, Height = 18 };
        icon.SetBinding(definition.Icon is null ? StudioX.Desktop.Icon.ForegroundProperty : PluginActivityIconView.ForegroundProperty,
            new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        var button = new Button
        {
            Content = icon,
            Style = (Style)FindResource("RailButton"),
            ToolTip = definition.Tooltip ?? title + (string.IsNullOrEmpty(active.Manifest.Description) ? "" : "\n" + active.Manifest.Description)
        };
        AutomationProperties.SetName(button, "插件：" + title);
        var body = new StackPanel { Margin = new Thickness(16, 6, 16, 16) };
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var tab = new TabItem { Content = scroll, Visibility = Visibility.Collapsed, Height = 34, Padding = new Thickness(12, 4, 8, 4) };
        tab.Header = CreateTabHeader(tab, new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        AutomationProperties.SetName(tab, title);
        AttachTabMouseActions(tab);
        WorkspaceTabs.Items.Add(tab);
        var activity = new PluginActivity(button, tab, body);
        pluginActivities.Add(active.Id, activity);
        PluginActivityRail.Children.Add(button);
        button.Click += (_, _) =>
        {
            if (pluginActivities.TryGetValue(active.Id, out var current) && ReferenceEquals(current, activity))
            {
                ShowDocument(tab);
            }
        };
        tab.IsVisibleChanged += (_, _) => RefreshPluginActivitySelection();
        if (active.Contribution.Panels.Length == 0)
        {
            var hint = new TextBlock { Text = "此插件未提供面板，可从插件管理页或命令面板使用其命令。", TextWrapping = TextWrapping.Wrap };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            body.Children.Add(hint);
        }
    }

    private void RefreshPluginActivitySelection()
    {
        foreach (var item in pluginActivities.Values)
        {
            item.Button.SetResourceReference(Control.ForegroundProperty, item.Tab.IsSelected && item.Tab.Visibility == Visibility.Visible ? "Accent" : "Muted");
            item.Button.SetResourceReference(Control.BackgroundProperty, item.Tab.IsSelected && item.Tab.Visibility == Visibility.Visible ? "HoverSurface" : "ChromeSurface");
        }
    }

    private void RemovePluginActivity(string id)
    {
        if (!pluginActivities.Remove(id, out var activity))
        {
            return;
        }
        PluginActivityRail.Children.Remove(activity.Button);
        var selected = WorkspaceTabs.SelectedItem == activity.Tab;
        activity.Tab.Content = null;
        WorkspaceTabs.Items.Remove(activity.Tab);
        if (selected)
        {
            ShowDocument(WelcomeTab);
        }
    }

    private void ClearPluginActivities()
    {
        foreach (var id in pluginActivities.Keys.ToArray())
        {
            RemovePluginActivity(id);
        }
    }

    private sealed record PluginActivity(Button Button, TabItem Tab, StackPanel Body);
}
