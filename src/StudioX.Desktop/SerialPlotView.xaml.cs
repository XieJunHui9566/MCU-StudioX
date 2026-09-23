namespace StudioX.Desktop;

using System.Globalization;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using StudioX.Application.SerialPlot;
using StudioX.Devices;

public partial class SerialPlotView : UserControl
{
    private SerialPlotService? service;
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly List<TextBlock> values = [];
    private PlotSnapshot? displayed;
    private string lastServiceError = "";
    private bool paused, busy, shutdown;
    private Task operation = Task.CompletedTask;
    private sealed record Choice<T>(T Value, string Label);
    public SerialPlotView()
    {
        InitializeComponent();
        BitsPicker.ItemsSource = new[] { 5, 6, 7, 8 }; BitsPicker.SelectedItem = 8;
        ParityPicker.ItemsSource = new[] { new Choice<Parity>(Parity.None, "无"), new(Parity.Odd, "奇校验"), new(Parity.Even, "偶校验"), new(Parity.Mark, "Mark"), new(Parity.Space, "Space") }; ParityPicker.SelectedIndex = 0;
        StopPicker.ItemsSource = new[] { new Choice<StopBits>(StopBits.One, "1"), new(StopBits.OnePointFive, "1.5"), new(StopBits.Two, "2") }; StopPicker.SelectedIndex = 0;
        FlowPicker.ItemsSource = new[] { new Choice<Handshake>(Handshake.None, "无"), new(Handshake.RequestToSend, "RTS / CTS"), new(Handshake.XOnXOff, "XON / XOFF"), new(Handshake.RequestToSendXOnXOff, "RTS/CTS + XON") }; FlowPicker.SelectedIndex = 0;
        Chart.ViewChanged += () => { FollowCheck.IsChecked = Chart.Follow; AutoYCheck.IsChecked = Chart.AutoY; };
        refresh.Tick += (_, _) => { if (IsVisible && !shutdown) RefreshView(); };
        refresh.Start();
    }
    public void Attach(SerialPlotService source)
    {
        service = source;
        operation = RunAsync(async () =>
        {
            var p = await source.LoadPreferencesAsync();
            RefreshPorts(p.Connection.PortName);
            BaudInput.Text = p.Connection.BaudRate.ToString(CultureInfo.InvariantCulture); BitsPicker.SelectedItem = p.Connection.DataBits;
            ParityPicker.SelectedValue = p.Connection.Parity; StopPicker.SelectedValue = p.Connection.StopBits; FlowPicker.SelectedValue = p.Connection.FlowControl;
            FieldInput.Text = p.Format.FieldSeparator; RecordInput.Text = p.Format.RecordSeparator; IntervalInput.Text = p.Format.SampleIntervalMs.ToString(CultureInfo.InvariantCulture);
        });
    }
    private void RefreshPorts(string? preferred = null)
    {
        var selected = preferred ?? PortPicker.SelectedItem as string;
        PortPicker.ItemsSource = SerialPlotService.GetPorts();
        PortPicker.SelectedItem = selected;
        if (PortPicker.SelectedIndex < 0 && PortPicker.Items.Count > 0) PortPicker.SelectedIndex = 0;
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) { try { RefreshPorts(); } catch (Exception ex) { Error(ex); } }
    private PlotPreferences ReadPreferences() => new(new(PortPicker.SelectedItem as string ?? "",
        int.Parse(BaudInput.Text, CultureInfo.InvariantCulture), (int)BitsPicker.SelectedItem,
        (Parity)ParityPicker.SelectedValue, (StopBits)StopPicker.SelectedValue, (Handshake)FlowPicker.SelectedValue,
        DtrCheck.IsChecked == true, RtsCheck.IsChecked == true),
        new(FieldInput.Text, RecordInput.Text, double.Parse(IntervalInput.Text, CultureInfo.InvariantCulture)));
    private void Connect_Click(object sender, RoutedEventArgs e) => Start(false);
    private void Demo_Click(object sender, RoutedEventArgs e) => Start(true);
    private void Start(bool demo)
    {
        if (busy || service is null || shutdown) return;
        operation = RunAsync(async () =>
        {
            if (service.Snapshot().Connected) { await service.DisconnectAsync(); return; }
            var p = ReadPreferences();
            await service.ConnectAsync(p.Connection, p.Format, demo);
            paused = false; PauseButton.Content = "暂停显示"; displayed = null; Chart.ResetView();
            Notice.Text = "同一端口只能由一个工具占用；从串口终端切换时请先断开终端连接。";
            await service.SavePreferencesAsync(p);
        });
    }
    private async Task RunAsync(Func<Task> action)
    {
        busy = true; UpdateButtons(service?.Snapshot().Connected == true);
        try { await action(); }
        catch (Exception ex) { Error(ex); }
        finally { busy = false; RefreshView(); }
    }
    private void Error(Exception ex) { Notice.Text = ex.Message.Split('\n')[0]; Notice.ToolTip = ex.ToString(); }
    private void UpdateButtons(bool connected)
    {
        Parameters.IsEnabled = FormatParameters.IsEnabled = !busy && !connected;
        ConnectButton.IsEnabled = !busy; ConnectButton.Content = connected ? "停止采集" : "开始采集";
        DemoButton.IsEnabled = !busy && !connected;
    }
    private void RefreshView(bool force = false)
    {
        if (service is null) return;
        var s = service.Snapshot(); UpdateButtons(s.Connected);
        ConnectionLabel.Text = s.Message + (s.Simulated && !s.Connected ? " · 演示数据" : "") + (paused ? " · 显示已暂停" : "");
        Statistics.Text = $"RX {s.ReceivedBytes:N0} B · 有效 {s.ValidRecords:N0} 组 · 错误 {s.InvalidRecords:N0} · 丢包 {s.DroppedFrames:N0} · 淘汰旧记录 {s.EvictedRecords:N0} · " +
            (s.Format.SampleIntervalMs > 0 ? $"时间：设定周期 {s.Format.SampleIntervalMs:g} ms（丢包后间隔未知）" : "时间：电脑接收时钟（同包数据可能同一时间）");
        Statistics.ToolTip = Statistics.Text;
        if (lastServiceError != s.LastError)
        {
            lastServiceError = s.LastError ?? "";
            // 历史接收错误不能在每次刷新时覆盖当前参数 / 文件操作的诊断。
            if (lastServiceError.Length > 0) { Notice.Text = lastServiceError.Split('\n')[0]; Notice.ToolTip = lastServiceError; }
        }
        if (!force && (paused || displayed?.Version == s.Version)) return;
        displayed = s; Chart.SetSamples(s.Samples, s.Channels);
        if (values.Count != s.Channels)
        {
            Legend.Children.Clear(); values.Clear();
            for (var c = 0; c < s.Channels; c++)
            {
                var channel = c;
                var label = new TextBlock { Foreground = SerialPlotChart.ChannelBrushes[c], FontFamily = new FontFamily("Consolas"), MinWidth = 148 };
                values.Add(label);
                var check = new CheckBox { Content = label, IsChecked = Chart.VisibleChannels[c], Margin = new Thickness(0, 0, 12, 5) };
                check.Click += (_, _) => { Chart.VisibleChannels[channel] = check.IsChecked == true; Chart.InvalidateVisual(); };
                Legend.Children.Add(check);
            }
        }
        for (var c = 0; c < values.Count; c++) values[c].Text = $"CH{c + 1}  " + (s.Samples.Length == 0 ? "—" : s.Samples[^1].Values[c].ToString("G8", CultureInfo.InvariantCulture));
    }
    private void Pause_Click(object sender, RoutedEventArgs e) { paused = !paused; PauseButton.Content = paused ? "继续显示" : "暂停显示"; RefreshView(); }
    private void Clear_Click(object sender, RoutedEventArgs e) { service?.Clear(); Notice.Text = "已清空，等待下一组完整数据。"; Chart.ResetView(); RefreshView(true); }
    private void ViewOption_Click(object sender, RoutedEventArgs e) { Chart.Follow = FollowCheck.IsChecked == true; Chart.AutoY = AutoYCheck.IsChecked == true; Chart.InvalidateVisual(); }
    private void Reset_Click(object sender, RoutedEventArgs e) => Chart.ResetView();
    private void TimeIn_Click(object sender, RoutedEventArgs e) => Chart.Zoom(false, .8);
    private void TimeOut_Click(object sender, RoutedEventArgs e) => Chart.Zoom(false, 1.25);
    private void ValueIn_Click(object sender, RoutedEventArgs e) => Chart.Zoom(true, .8);
    private void ValueOut_Click(object sender, RoutedEventArgs e) => Chart.Zoom(true, 1.25);
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (busy || displayed is null || displayed.Samples.Length == 0) return;
        var dialog = new SaveFileDialog { Filter = "CSV 数据 (*.csv)|*.csv", FileName = "serial-plot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var snapshot = displayed;
        operation = RunAsync(async () => { await SerialPlotService.ExportCsvAsync(dialog.FileName, snapshot); Notice.Text = $"已导出 {snapshot.Samples.Length:N0} 组数据：{dialog.FileName}"; });
    }
    internal bool HandlePlotMouseWheel(DependencyObject? hit, int delta, bool control)
    {
        for (var node = hit; node is not null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node == Chart) return Chart.HandleWheel(delta, control);
        return false;
    }
    public void RefreshTheme() => Chart.InvalidateVisual();
    public async Task CloseSessionAsync() { await operation; if (service is not null) await service.DisconnectAsync(); RefreshView(); }
    public async Task ShutdownAsync() { shutdown = true; refresh.Stop(); await CloseSessionAsync(); }
}
