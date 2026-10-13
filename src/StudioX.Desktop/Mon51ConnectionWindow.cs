namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Application.StcDebugging;

/// <summary>先制作并重新上电，再编译更新用户程序；窗口不持有串口。</summary>
public sealed class Mon51ConnectionWindow : Window
{
    private readonly ComboBox ports = new() { MinWidth = 180 };
    private readonly ComboBox baud = new() { ItemsSource = new[] { 115200, 57600, 102400 }, SelectedIndex = 0 };
    private readonly ComboBox target = new() { ItemsSource = new[] { "IAP15F2K61S2" }, SelectedIndex = -1 };
    private readonly ComboBox firmware = new() { DisplayMemberPath = nameof(Mon51FirmwareSource.DisplayName) };
    private readonly CheckBox configured = new() { Content = new TextBlock { Text = "已成功设置为仿真芯片，并在设置完成后给 MCU 断电约 2 秒再上电", TextWrapping = TextWrapping.Wrap }, Margin = new(0, 12, 0, 10) };
    private readonly TextBox detail = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 95, MaxHeight = 170 };
    private readonly Button setup = new() { Content = "设置为仿真芯片（覆盖程序）", Padding = new(12, 5, 12, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 8, 0, 8), IsEnabled = false };
    private readonly Button start = new() { Content = "开始调试", Padding = new(14, 5, 14, 5), IsEnabled = false };
    private readonly Button cancel = new() { Content = "关闭", Padding = new(14, 5, 14, 5), Margin = new(10, 0, 0, 0) };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? attempt;
    private bool closeWhenIdle;
    private bool working;

    public Mon51ConnectionWindow(string[] availablePorts, string selectedPort, string projectName,
        IReadOnlyList<Mon51FirmwareSource> firmwareSources,
        Func<string, int, string, IProgress<string>, CancellationToken, Task> startDebug,
        Func<string, string, Mon51FirmwareSource, IProgress<string>, CancellationToken, Task> setupMonitor)
    {
        Title = "STC Mon51 仿真调试";
        Width = 650;
        Height = 680;
        MinWidth = 480;
        MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "ChromeSurface");
        SetResourceReference(ForegroundProperty, "Text");
        ports.ItemsSource = availablePorts;
        firmware.ItemsSource = firmwareSources;
        // 仅有一个明确资源或工程锁定包提供资源时可默认；不猜测多个包中的最新版本。
        firmware.SelectedItem = firmwareSources.Count == 1 ? firmwareSources[0] : firmwareSources.SingleOrDefault(s => s.IsProjectPack);
        if (availablePorts.Contains(selectedPort, StringComparer.OrdinalIgnoreCase))
        {
            ports.SelectedItem = availablePorts.First(p => p.Equals(selectedPort, StringComparison.OrdinalIgnoreCase));
        }
        var body = new StackPanel { Margin = new(18) };
        AddText(body, $"STC Mon51 仿真调试 · {projectName}", true);
        AddField(body, "确认实机型号（Mon51 协议不能读取准确料号）", target);
        AddField(body, "串口（请先释放串口终端及其他软件的连接）", ports);
        AddText(body, "1. 制作仿真芯片", true);
        AddField(body, "监控固件（器件包提供）", firmware);
        AddText(body, firmwareSources.Count == 0
            ? "当前没有兼容监控固件。请在 IDE 中导入包含 IAP15F2K61S2 / Mon51 固件的 .mcupack，再打开此窗口。已制作且监控生效的芯片可直接进行第二步。"
            : "IDE 直接读取所选器件包中的 Mon51 V2.5 固件并下载，无需其他程序。监控包独立选择，不改变工程的编译工具或 SDK 锁定。");
        AddText(body, "制作会覆盖用户程序和监控区，并写回读取到的选项与时钟参数。当前仅适配 IAP15F2K61S2 / ISP 7.2.5S / 状态标记 70。已制作且监控生效可跳过此步。");
        body.Children.Add(setup);
        AddText(body, "制作成功后，必须给 MCU 本体断电再上电；CH340 通常不能自动断电，按复位键不能代替。");
        AddText(body, "2. 更新程序并开始调试", true);
        AddText(body, "点击开始调试后自动保存、编译最新源码，通过 Mon51 覆盖用户程序，回读核对并加载源码符号，保持暂停。之后可设置断点、运行及单步。监控生效后更新用户程序通常无需断电。");
        AddField(body, "监控通信波特率 · 8N1 · 无流控", baud);
        body.Children.Add(configured);
        AddText(body, "资源边界：保留末尾 6 KB Flash、768 字节 XDATA；用户 CODE 至 DBFC，XDATA 1 KB。P3.0 / P3.1 专供监控，程序须避开其写入和 INT4 / 定时器 2 相关功能。普通下载走 ISP，之后可能需要重新制作仿真芯片。");
        body.Children.Add(detail);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(18, 0, 18, 18) };
        buttons.Children.Add(start);
        buttons.Children.Add(cancel);
        var layout = new Grid();
        layout.RowDefinitions.Add(new()
        {
            Height = new(1, GridUnitType.Star)
        });
        layout.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetRow(buttons, 1);
        layout.Children.Add(buttons);
        Content = layout;
        void Update()
        {
            var selected = ports.SelectedItem is string && target.SelectedItem is string;
            setup.IsEnabled = !working && selected && firmware.SelectedItem is Mon51FirmwareSource;
            start.IsEnabled = !working && selected && configured.IsChecked == true;
        }
        ports.SelectionChanged += (_, _) => { configured.IsChecked = false; Update(); };
        target.SelectionChanged += (_, _) => { configured.IsChecked = false; Update(); };
        firmware.SelectionChanged += (_, _) => { configured.IsChecked = false; Update(); };
        configured.Checked += (_, _) => Update();
        configured.Unchecked += (_, _) => Update();
        setup.Click += async (_, _) => await PerformAsync(true);
        start.Click += async (_, _) => await PerformAsync(false);
        async Task PerformAsync(bool making)
        {
            var selectedFirmware = firmware.SelectedItem as Mon51FirmwareSource;
            if (working || ports.SelectedItem is not string port || target.SelectedItem is not string model ||
                making && selectedFirmware is null || !making && configured.IsChecked != true)
            {
                return;
            }
            working = true;
            if (making)
            {
                configured.IsChecked = false;
            }
            Update();
            ports.IsEnabled = baud.IsEnabled = target.IsEnabled = firmware.IsEnabled = configured.IsEnabled = false;
            cancel.Content = "取消操作";
            detail.Text = making ? "准备制作；工具开始等待后，请给 MCU 断电再上电进入 ISP。" : "正在保存并编译最新源码…";
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            attempt = operation;
            var progress = new Progress<string>(message =>
            {
                if (!working)
                {
                    return;
                }
                detail.AppendText("\n" + message);
                if (detail.Text.Length > 32000)
                {
                    detail.Text = detail.Text[^16000..];
                }
                detail.ScrollToEnd();
            });
            try
            {
                if (making && selectedFirmware is { } source)
                {
                    await setupMonitor(port, model, source, progress, operation.Token);
                    detail.Text = "制作写入应答成功。请给 MCU 本体断电约 2 秒再上电；完成后勾选上方确认，再点击开始调试。监控是否生效将在连接时验证。";
                }
                else
                {
                    await startDebug(port, (int)baud.SelectedItem, model, progress, operation.Token);
                    working = false;
                    DialogResult = true;
                }
            }
            catch (OperationCanceledException)
            {
                detail.Text = making ? "制作已取消。若已开始擦写，程序或监控可能不完整；请查看日志后重新制作。" : "启动已取消；串口已释放，可修改参数后重试。";
            }
            catch (Exception ex) { detail.Text = ex.Message + "\n\n原始诊断：\n" + ex; }
            finally
            {
                attempt = null;
                working = false;
                ports.IsEnabled = baud.IsEnabled = target.IsEnabled = firmware.IsEnabled = configured.IsEnabled = true;
                cancel.Content = "关闭";
                cancel.IsEnabled = true;
                Update();
                if (closeWhenIdle)
                {
                    Close();
                }
            }
        }
        cancel.Click += (_, _) => { if (working) { attempt?.Cancel(); cancel.IsEnabled = false; } else { Close(); } };
        Closing += (_, e) => { if (working) { e.Cancel = true; closeWhenIdle = true; attempt?.Cancel(); } };
        Closed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
    }

    private static void AddText(Panel body, string text, bool heading = false) => body.Children.Add(new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new(0, 9, 0, 0),
        FontSize = heading ? 16 : 12,
        FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal
    });
    private static void AddField(Panel body, string label, Control control)
    {
        body.Children.Add(new TextBlock { Text = label, Margin = new(0, 8, 0, 4) });
        body.Children.Add(control);
    }
}
