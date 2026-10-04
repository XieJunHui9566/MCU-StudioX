namespace StudioX.Desktop;

using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using StudioX.Application.OpenOcdPlot;

public partial class OpenOcdPlotView : UserControl
{
    private static readonly Brush[] LightPalette = new[] { "#0073AD", "#B45C00", "#007A46", "#8543BE", "#C33258", "#807000", "#007B80", "#425BB8" }
        .Select(value => { var brush = (Brush)new BrushConverter().ConvertFromString(value)!; brush.Freeze(); return brush; }).ToArray();
    private OpenOcdPlotService? service;
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private Task operation = Task.CompletedTask;
    private bool busy;
    internal Task PendingOperation => operation;

    public OpenOcdPlotView()
    {
        InitializeComponent();
        TypePicker.ItemsSource = Enum.GetValues<PlotScalar>();
        TypePicker.SelectedItem = PlotScalar.Auto;
        TypePicker.ToolTip = "Auto 按 ELF 类型识别；自定义类型别名可手动选择";
        refresh.Tick += (_, _) => RefreshView();
        Loaded += (_, _) => { refresh.Start(); RefreshView(); };
        Unloaded += (_, _) => refresh.Stop();
        Chart.ViewChanged += () => { FollowCheck.IsChecked = Chart.Follow; AutoYCheck.IsChecked = Chart.AutoY; };
    }

    public void Attach(OpenOcdPlotService value)
    {
        service = value;
        RefreshView();
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (busy)
        {
            return;
        }
        busy = true;
        Notice.Text = "";
        RefreshView();
        try
        {
            await action();
        }
        catch (Exception ex) { Notice.Text = ex.Message; Notice.ToolTip = ex.ToString(); }
        finally { busy = false; RefreshView(); }
    }
    private void Add_Click(object sender, RoutedEventArgs e) => operation = RunAsync(async () =>
    {
        await service!.AddAsync(ExpressionInput.Text, (PlotScalar)TypePicker.SelectedItem);
        ExpressionInput.Clear();
        Chart.ResetView();
    });
    private void Remove_Click(object sender, RoutedEventArgs e) => operation = RunAsync(() => { service!.Remove(ChannelsGrid.SelectedIndex); return Task.CompletedTask; });
    private void ClearChannels_Click(object sender, RoutedEventArgs e) => operation = RunAsync(() => { service!.Clear(true); Chart.ResetView(); return Task.CompletedTask; });
    private void ClearData_Click(object sender, RoutedEventArgs e) => operation = RunAsync(() => { service!.Clear(false); Chart.ResetView(); return Task.CompletedTask; });
    private void Start_Click(object sender, RoutedEventArgs e) => operation = RunAsync(async () =>
    {
        if (!int.TryParse(IntervalInput.Text, out var interval))
        {
            throw new ArgumentException("请输入 20–10000 ms 的整数采样间隔。");
        }
        await service!.StartAsync(interval);
        Chart.ResetView();
    });
    private void Stop_Click(object sender, RoutedEventArgs e) => operation = RunAsync(() => service!.StopAsync());
    private void Reset_Click(object sender, RoutedEventArgs e) => Chart.ResetView();
    private void Options_Click(object sender, RoutedEventArgs e)
    {
        Chart.Follow = FollowCheck.IsChecked == true;
        Chart.AutoY = AutoYCheck.IsChecked == true;
        Chart.InvalidateVisual();
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV 数据 (*.csv)|*.csv", FileName = "openocd-plot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        operation = RunAsync(() => File.WriteAllTextAsync(dialog.FileName, service!.ExportCsv(), new UTF8Encoding(true)));
    }

    internal void RefreshView()
    {
        if (service is null)
        {
            return;
        }
        Display(service.Snapshot());
    }
    internal void Display(OpenOcdPlotSnapshot snapshot)
    {
        TargetLabel.Text = snapshot.Target.Length == 0 ? "尚未添加实机变量" : snapshot.Target;
        StatusLabel.Text = snapshot.Status;
        StatisticsLabel.Text = $"缓存 {snapshot.Records.Length:N0} / {OpenOcdPlotService.Capacity:N0} 组 · 间隔 {snapshot.IntervalMs} ms · 错过周期 {snapshot.MissedIntervals:N0} · 淘汰 {snapshot.EvictedRecords:N0} · 无效数值 {snapshot.InvalidRecords:N0} · 主机时间";
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, snapshot.Error is null ? "Text" : "DiagnosticError");
        StatusLabel.ToolTip = snapshot.Error;
        var selected = ChannelsGrid.SelectedIndex;
        var last = snapshot.Records.LastOrDefault();
        ChannelsGrid.ItemsSource = snapshot.Channels.Select((channel, index) => new
        {
            Legend = "CH" + (index + 1),
            Brush = Chart.Palette[index],
            channel.Expression,
            Address = channel.AddressText,
            Type = channel.Type.ToString(),
            Value = last is null ? "—" : last.Values[index].ToString("G9", CultureInfo.InvariantCulture)
        }).ToArray();
        ChannelsGrid.SelectedIndex = selected;
        Chart.SetSamples(snapshot.Samples, snapshot.Channels.Length);
        AddButton.IsEnabled = RemoveButton.IsEnabled = ClearChannelsButton.IsEnabled = ClearDataButton.IsEnabled =
            ExpressionInput.IsEnabled = TypePicker.IsEnabled = IntervalInput.IsEnabled = !busy && !snapshot.Capturing;
        StartButton.IsEnabled = !busy && !snapshot.Capturing && snapshot.Channels.Length > 0;
        StopButton.IsEnabled = !busy && snapshot.Capturing;
        ExportButton.IsEnabled = !busy && snapshot.Records.Length > 0;
    }
    internal bool HandlePlotMouseWheel(DependencyObject? hit, int delta, bool control)
    {
        for (var node = hit; node is not null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node == Chart)
            {
                return Chart.HandleWheel(delta, control);
            }
        }
        return false;
    }
    public async Task CloseSessionAsync()
    {
        await operation;
        if (service is not null)
        {
            await service.StopAsync();
        }
        RefreshView();
    }
    public void RefreshTheme()
    {
        var color = (TryFindResource("EditorSurface") as SolidColorBrush)?.Color ?? Colors.Black;
        Chart.Palette = color.R * .2126 + color.G * .7152 + color.B * .0722 > 150 ? LightPalette : SerialPlotChart.ChannelBrushes;
        RefreshView();
        Chart.InvalidateVisual();
    }
}
