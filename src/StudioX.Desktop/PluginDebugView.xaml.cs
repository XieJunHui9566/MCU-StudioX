namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Application.Plugins;
using StudioX.Engine.Debugging;

/// <summary>呈现经应用服务校验的只读扩展数据，不加载插件程序集。</summary>
public partial class PluginDebugView : UserControl
{
    private readonly PluginPanelRenderer renderer;
    public Action? RefreshRequested
    {
        get; set;
    }
    public PluginDebugView(string title, string identity, Action<string> log)
    {
        InitializeComponent();
        TitleText.Text = title;
        PluginIdentity.Text = identity;
        renderer = new PluginPanelRenderer((_, _) => Task.CompletedTask, log);
    }
    public void Update(PluginDebugViewUpdate update)
    {
        StateText.Text = update.Status;
        RefreshButton.IsEnabled = update.State == DebugState.Stopped;
        PanelHost.Content = update.Panel is null ? null : renderer.Render(update.Panel);
        DiagnosticText.Text = update.Diagnostic ?? "";
        DiagnosticText.Visibility = update.Diagnostic is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
}
