namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StudioX.Application;

/// <summary>明确选择连接模式并展示应用服务阶段；窗口不拥有设备。</summary>
public sealed class DebugConnectionWindow : Window
{
    private readonly DebugLaunchService service;
    private readonly Func<bool, CancellationToken, Task> connect;
    private readonly CancellationTokenSource cancellation = new();
    private readonly CheckBox reset = new() { Content = "复位连接（仅 STM32 + ST-Link，需要连接 NRST，会复位并暂停）", Margin = new(0, 8, 0, 8) };
    private readonly Button start = new() { Content = "开始连接", Padding = new(12, 6, 12, 6) };
    private readonly Button cancel = new() { Content = "关闭", Margin = new(8, 0, 0, 0), Padding = new(12, 6, 12, 6) };
    private readonly DataGrid grid = new() { IsReadOnly = true, AutoGenerateColumns = true, CanUserAddRows = false, MinHeight = 210, MinRowHeight = 30 };
    private readonly TextBox detail = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 90 };
    private readonly TextBlock elapsed = new() { Margin = new(0, 8, 0, 8) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool working;

    public DebugConnectionWindow(DebugLaunchService service, string target, bool allowReset, Func<bool, CancellationToken, Task> connect, bool review = false)
    {
        this.service = service;
        this.connect = connect;
        Title = "调试连接与恢复";
        Width = 880;
        Height = 620;
        MinWidth = 620;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "ChromeSurface");
        SetResourceReference(ForegroundProperty, "Text");
        grid.SetResourceReference(StyleProperty, "DebugGrid");
        var body = new Grid { Margin = new(16) };
        body.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        body.RowDefinitions.Add(new()
        {
            Height = new(3, GridUnitType.Star)
        });
        body.RowDefinitions.Add(new()
        {
            Height = new(2, GridUnitType.Star)
        });
        body.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = target, FontSize = 18, TextWrapping = TextWrapping.Wrap });
        top.Children.Add(new TextBlock { Text = "普通附加会暂停目标；校验通过后进入源码调试。连接过程不下载固件。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) });
        reset.IsEnabled = allowReset && !review;
        start.IsEnabled = !review;
        top.Children.Add(reset);
        top.Children.Add(elapsed);
        body.Children.Add(top);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 10, 0, 0) };
        buttons.Children.Add(start);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 3);
        body.Children.Add(buttons);
        Grid.SetRow(detail, 2);
        body.Children.Add(detail);
        Grid.SetRow(grid, 1);
        body.Children.Add(grid);
        Content = body;
        start.Click += async (_, _) => await ConnectAsync();
        cancel.Click += (_, _) => { if (working) { cancellation.Cancel(); cancel.IsEnabled = false; } else { Close(); } };
        service.Changed += OnChanged;
        timer.Tick += (_, _) => Update();
        timer.Start();
        Closing += (_, e) => { if (working) { e.Cancel = true; cancellation.Cancel(); } };
        Closed += (_, _) => { service.Changed -= OnChanged; timer.Stop(); cancellation.Dispose(); };
        Update();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Update);
    private void Update()
    {
        var report = service.Current;
        grid.ItemsSource = report.Steps.Select(s => new { 阶段 = s.Name, 状态 = s.Status, 说明 = s.Detail }).ToArray();
        elapsed.Text = report.Busy ? $"已等待 {(DateTimeOffset.UtcNow - report.StartedAtUtc).TotalSeconds:0} 秒 · 时间来自宿主" : "连接过程可以取消；结果保留供排查。";
        detail.Text = (report.LogPath is null ? "" : "原始日志：" + report.LogPath + "\n") + report.Diagnostic;
    }

    private async Task ConnectAsync()
    {
        working = true;
        start.IsEnabled = reset.IsEnabled = false;
        cancel.Content = "取消连接";
        try
        {
            await connect(reset.IsChecked == true, cancellation.Token);
        }
        catch (Exception error) { detail.Text = error.ToString(); }
        finally
        {
            working = false;
            cancel.Content = "关闭";
            cancel.IsEnabled = true;
            Update();
        }
    }
}
