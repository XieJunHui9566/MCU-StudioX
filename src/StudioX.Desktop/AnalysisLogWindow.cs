namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StudioX.Application.CodeIntelligence;

public sealed class AnalysisLogWindow : Window
{
    private readonly CodeIntelligenceService service;
    internal TextBox LogText
    {
        get;
    } = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        AcceptsTab = true,
        FontFamily = new FontFamily("Cascadia Mono"),
        FontSize = 13,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Padding = new Thickness(10)
    };
    public AnalysisLogWindow(CodeIntelligenceService service)
    {
        this.service = service;
        Title = "语言分析日志";
        Width = 980;
        Height = 620;
        MinWidth = 600;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface");
        SetResourceReference(ForegroundProperty, "Text");
        LogText.SetResourceReference(BackgroundProperty, "EditorSurface");
        LogText.SetResourceReference(ForegroundProperty, "Text");
        LogText.SetResourceReference(BorderBrushProperty, "Border");
        var panel = new DockPanel { Margin = new Thickness(12) };
        var description = new TextBlock
        {
            Text = "后台补全、悬停与诊断的原始消息保存在这里；按需刷新查看。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(description, Dock.Top);
        panel.Children.Add(description);
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        var refresh = new Button { Content = "刷新", MinWidth = 70, Margin = new Thickness(8, 0, 0, 0) };
        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = false;
            try
            {
                await RefreshAsync();
            }
            catch (Exception error)
            {
                service.RecordAnalysisLog("查看语言分析日志：" + error);
                LogText.Text = "日志读取失败，原始异常：\n" + error;
            }
            finally { refresh.IsEnabled = true; }
        };
        var copy = new Button { Content = "复制日志路径", MinWidth = 105, Margin = new Thickness(8, 0, 0, 0) };
        copy.Click += (_, _) => Clipboard.SetText(service.AnalysisLogPath);
        var close = new Button { Content = "关闭", MinWidth = 70, Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => Close();
        buttons.Children.Add(refresh);
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        footer.Children.Add(buttons);
        footer.Children.Add(new TextBlock { Text = service.AnalysisLogPath, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(footer);
        panel.Children.Add(LogText);
        Content = panel;
    }
    public async Task RefreshAsync(CancellationToken token = default)
    {
        LogText.Text = await service.ReadAnalysisLogAsync(token);
        LogText.ScrollToEnd();
    }
}
