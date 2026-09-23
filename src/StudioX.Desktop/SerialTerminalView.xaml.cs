namespace StudioX.Desktop;

using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Microsoft.Win32;
using StudioX.Application.Serial;
using StudioX.Devices;

public partial class SerialTerminalView : UserControl
{
    private sealed record Choice<T>(T Value, string Label);
    private SerialTerminalService? service;
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer repeat = new();
    private readonly TerminalColorizer colorizer = new();
    private readonly List<string> history = [];
    private bool ready, initialized, busy, sending, painting, paused, shuttingDown;
    private bool followSuspendedByWheel;
    private int receiveZoomDelta;
    private long renderedTrimmedLines;
    private long renderedVersion = -1;
    private int ticks;
    private Task displayTask = Task.CompletedTask;
    private Task preferenceTask = Task.CompletedTask;
    public SerialTerminalView()
    {
        InitializeComponent();
        BaudPicker.ItemsSource = new[] { "1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200", "230400", "460800", "921600", "1000000", "2000000" }; BaudPicker.Text = "115200";
        BitsPicker.ItemsSource = new[] { 5, 6, 7, 8 }; BitsPicker.SelectedItem = 8;
        StopPicker.ItemsSource = new[] { new Choice<StopBits>(StopBits.One, "1"), new(StopBits.OnePointFive, "1.5"), new(StopBits.Two, "2") }; StopPicker.SelectedValue = StopBits.One;
        ParityPicker.ItemsSource = new[] { new Choice<Parity>(Parity.None, "无 None"), new(Parity.Odd, "奇 Odd"), new(Parity.Even, "偶 Even"), new(Parity.Mark, "Mark"), new(Parity.Space, "Space") }; ParityPicker.SelectedValue = Parity.None;
        FlowPicker.ItemsSource = new[] { new Choice<Handshake>(Handshake.None, "无流控"), new(Handshake.RequestToSend, "RTS / CTS"), new(Handshake.XOnXOff, "XON / XOFF"), new(Handshake.RequestToSendXOnXOff, "RTS/CTS + XON/XOFF") }; FlowPicker.SelectedValue = Handshake.None;
        var encodings = new[] { new Choice<SerialTextMode>(SerialTextMode.Utf8, "UTF-8"), new(SerialTextMode.Gb2312, "GB2312 / GBK"), new(SerialTextMode.Hex, "HEX") };
        ReceiveMode.ItemsSource = encodings; SendMode.ItemsSource = encodings; ReceiveMode.SelectedIndex = SendMode.SelectedIndex = 0;
        EndingPicker.ItemsSource = new[] { new Choice<SerialLineEnding>(SerialLineEnding.None, "无"), new(SerialLineEnding.CrLf, "CRLF"), new(SerialLineEnding.Lf, "LF"), new(SerialLineEnding.Cr, "CR") }; EndingPicker.SelectedValue = SerialLineEnding.CrLf;
        ReceiveEditor.TextArea.TextView.LineTransformers.Add(colorizer);
        ICSharpCode.AvalonEdit.Search.SearchPanel.Install(ReceiveEditor);
        ReceiveEditor.PreviewMouseWheel += (_, e) =>
        {
            if (HandleReceiveMouseWheel(e.OriginalSource as DependencyObject, e.Delta, Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
                e.Handled = true;
        };
        refresh.Tick += async (_, _) => await PaintAsync();
        repeat.Tick += async (_, _) => { if (!sending) await SendAsync(); };
        Loaded += async (_, _) => await InitializeAsync();
        IsVisibleChanged += async (_, _) => { if (IsVisible) { await InitializeAsync(); renderedVersion = -1; await PaintAsync(); } };
    }
    // 原生滚轮按鼠标所在区域处理，发送框保持焦点时也能滚动接收区；WPF 事件作为后备入口。
    internal bool HandleReceiveMouseWheel(DependencyObject? hit, int delta, bool control)
    {
        if (!IsVisible || shuttingDown) return false;
        for (; hit is not null && hit != ReceiveEditor; hit = hit is Visual ? VisualTreeHelper.GetParent(hit) :
            hit is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(hit)) { }
        if (hit != ReceiveEditor || delta == 0) return false;
        if (control)
        {
            receiveZoomDelta += delta;
            var steps = receiveZoomDelta / Mouse.MouseWheelDeltaForOneLine;
            receiveZoomDelta %= Mouse.MouseWheelDeltaForOneLine;
            if (steps != 0) ReceiveEditor.FontSize = Math.Clamp(ReceiveEditor.FontSize + steps, 10, 28);
            return true;
        }
        receiveZoomDelta = 0;
        var lines = SystemParameters.WheelScrollLines;
        if (lines == 0) return true;
        var maximum = Math.Max(0, ReceiveEditor.ExtentHeight - ReceiveEditor.ViewportHeight);
        var distance = lines < 0 ? ReceiveEditor.ViewportHeight : lines * ReceiveEditor.TextArea.TextView.DefaultLineHeight;
        var offset = Math.Clamp(ReceiveEditor.VerticalOffset - delta / (double)Mouse.MouseWheelDeltaForOneLine * distance, 0, maximum);
        // 新数据不得把正在查看的历史拉回末尾；用户主动关闭自动滚动时不擅自重新打开。
        if (delta > 0 && maximum > 0 && FollowCheck.IsChecked == true)
        {
            followSuspendedByWheel = true;
            FollowCheck.IsChecked = false;
        }
        ReceiveEditor.ScrollToVerticalOffset(offset);
        if (delta < 0 && followSuspendedByWheel && offset >= maximum - 1)
        {
            followSuspendedByWheel = false;
            FollowCheck.IsChecked = true;
        }
        return true;
    }
    private void Follow_Click(object sender, RoutedEventArgs e)
    {
        followSuspendedByWheel = false;
        if (FollowCheck.IsChecked == true) ReceiveEditor.ScrollToEnd();
    }
    public void Attach(SerialTerminalService value) { service = value; ProtocolView.Attach(value); }
    private async Task InitializeAsync()
    {
        if (initialized || service is null) return; initialized = true;
        try
        {
            var prefs = await service.LoadPreferencesAsync(); var c = prefs.Connection;
            BaudPicker.Text = c.BaudRate.ToString(); BitsPicker.SelectedItem = c.DataBits; StopPicker.SelectedValue = c.StopBits; ParityPicker.SelectedValue = c.Parity; FlowPicker.SelectedValue = c.FlowControl;
            ReceiveMode.SelectedValue = prefs.ReceiveMode; SendMode.SelectedValue = prefs.SendMode; EndingPicker.SelectedValue = prefs.Ending;
            AnsiCheck.IsChecked = prefs.Ansi; TimeCheck.IsChecked = prefs.Timestamps; EscapeCheck.IsChecked = prefs.Escapes;
            history.AddRange(prefs.History?.Take(30) ?? []); UpdateHistory();
            await RefreshPortsAsync(c.PortName);
        }
        catch (Exception ex) { ShowError(ex); }
        ready = true; UpdateFlow(); await ChangeDisplayAsync(); refresh.Start();
    }
    private T Selected<T>(ComboBox box) where T : struct => box.SelectedValue is T value ? value : throw new ArgumentException("请选择有效的串口参数。");
    private SerialSettings Settings() => new(PortPicker.Text, int.TryParse(BaudPicker.Text, out var baud) ? baud : 0,
        BitsPicker.SelectedItem is int bits ? bits : 8, Selected<Parity>(ParityPicker), Selected<StopBits>(StopPicker), Selected<Handshake>(FlowPicker), DtrCheck.IsChecked == true, RtsCheck.IsChecked == true);
    private async Task RefreshPortsAsync(string? preferred = null)
    {
        var selected = preferred ?? PortPicker.Text; var ports = await SerialTerminalService.ListPortsAsync();
        PortPicker.ItemsSource = ports; PortPicker.SelectedItem = ports.Contains(selected, StringComparer.OrdinalIgnoreCase) ? selected : ports.FirstOrDefault();
        if (ports.Length == 0) Notice.Text = "未找到串口，请连接 USB 串口设备后刷新。";
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) { try { await RefreshPortsAsync(); } catch (Exception ex) { ShowError(ex); } }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (busy || service is null) return; busy = true; ConnectButton.IsEnabled = false;
        try
        {
            if (service.Status.Connected) await CloseSessionAsync();
            else { var settings = Settings(); settings.Validate(); await service.ConnectAsync(settings); await SavePreferencesAsync(); Notice.Text = "已连接，发送按钮可向设备写入数据。"; }
        }
        catch (Exception ex) { ShowError(ex); }
        finally { busy = false; ConnectButton.IsEnabled = true; await PaintAsync(); }
    }
    private async Task SavePreferencesAsync()
    {
        if (service is null || !ready) return;
        var prefs = new SerialPreferences(Settings(), Selected<SerialTextMode>(ReceiveMode), Selected<SerialTextMode>(SendMode), Selected<SerialLineEnding>(EndingPicker),
            AnsiCheck.IsChecked == true, TimeCheck.IsChecked == true, EscapeCheck.IsChecked == true, history.ToArray());
        var previous = preferenceTask;
        preferenceTask = Persist(); await preferenceTask;
        async Task Persist() { try { await previous; await service.SavePreferencesAsync(prefs); } catch (Exception ex) { ShowError(ex); } }
    }
    private async Task PaintAsync()
    {
        if (painting || service is null || shuttingDown) return; painting = true;
        try
        {
            if (++ticks % 5 == 0) await service.PollPinsAsync();
            var status = service.Status;
            ConnectButton.Content = status.Connected ? "断开串口" : "连接串口";
            ConnectionParameters.IsEnabled = PortPicker.IsEnabled = !status.Connected && !busy;
            SendButton.IsEnabled = status.Connected && !sending; RepeatCheck.IsEnabled = status.Connected;
            if (!status.Connected) StopRepeat();
            ConnectionStatus.Text = status.Message; ConnectionStatus.ToolTip = status.Message;
            PinsLabel.Text = status.Pins is { } pins ? $"CTS {(pins.Cts ? "1" : "0")}   DSR {(pins.Dsr ? "1" : "0")}   DCD {(pins.CarrierDetect ? "1" : "0")}" : "CTS —   DSR —   DCD —";
            Statistics.Text = $"RX {status.Received:N0} B    TX {status.Sent:N0} B";
            if (status.Errors.Length > 0 || status.DroppedFrames > 0 || status.DiscardedHistoryBytes > 0)
            {
                Notice.Text = $"丢帧 {status.DroppedFrames} · 历史淘汰 {status.DiscardedHistoryBytes:N0} B" + (status.Errors.LastOrDefault() is { } error ? " · " + error.Split('\n')[0] : "");
                Notice.ToolTip = string.Join('\n', status.Errors);
            }
            if (paused || !IsVisible) return;
            ProtocolView.RefreshFrames();
            var timestamps = TimeCheck.IsChecked == true;
            var snapshot = await Task.Run(() => service.ReadDisplay(renderedVersion, timestamps));
            if (snapshot is null || paused || !IsVisible) return;
            var scroll = ReceiveEditor.VerticalOffset; var horizontal = ReceiveEditor.HorizontalOffset;
            var selection = ReceiveEditor.SelectionStart; var length = ReceiveEditor.SelectionLength;
            // 淘汰顶部旧行后修正阅读位置，避免持续接收时历史内容逐渐向上跳动。
            scroll = Math.Max(0, scroll - Math.Max(0, snapshot.TrimmedLines - renderedTrimmedLines) * ReceiveEditor.TextArea.TextView.DefaultLineHeight);
            colorizer.Spans = snapshot.Spans; ReceiveEditor.Document.Text = snapshot.Text; renderedVersion = snapshot.Version;
            renderedTrimmedLines = snapshot.TrimmedLines;
            if (FollowCheck.IsChecked == true) ReceiveEditor.ScrollToEnd();
            else { ReceiveEditor.Select(Math.Min(selection, snapshot.Text.Length), Math.Min(length, Math.Max(0, snapshot.Text.Length - selection))); ReceiveEditor.ScrollToVerticalOffset(scroll); }
            ReceiveEditor.ScrollToHorizontalOffset(horizontal);
            if (snapshot.TrimmedLines > 0 && status.DiscardedHistoryBytes == 0) Notice.Text = $"显示保留最近 2000 行，已淘汰 {snapshot.TrimmedLines:N0} 行；原始缓存最多 2 MiB。";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { painting = false; }
    }
    private async Task ChangeDisplayAsync()
    {
        if (!ready || service is null) return;
        var mode = Selected<SerialTextMode>(ReceiveMode); var color = AnsiCheck.IsChecked == true; var tx = EchoCheck.IsChecked == true;
        var previous = displayTask;
        displayTask = Change(); await displayTask;
        async Task Change() { try { await previous; await service.SetDisplayAsync(mode, color, tx); renderedVersion = -1; await PaintAsync(); } catch (Exception ex) { ShowError(ex); } }
    }
    private async void Display_Changed(object sender, SelectionChangedEventArgs e) => await ChangeDisplayAsync();
    private async void Display_Click(object sender, RoutedEventArgs e) => await ChangeDisplayAsync();
    private async void Timestamp_Click(object sender, RoutedEventArgs e) { renderedVersion = -1; await PaintAsync(); }
    private void Flow_Changed(object sender, SelectionChangedEventArgs e) { if (ready) UpdateFlow(); }
    private void UpdateFlow() => RtsCheck.IsEnabled = FlowPicker.SelectedValue is not (Handshake.RequestToSend or Handshake.RequestToSendXOnXOff);
    private async void Outputs_Click(object sender, RoutedEventArgs e) { try { if (service is not null) await service.SetOutputsAsync(DtrCheck.IsChecked == true, RtsCheck.IsChecked == true); } catch (Exception ex) { ShowError(ex); } }
    private async Task SendAsync()
    {
        if (sending || service is null) return; sending = true;
        try
        {
            var bytes = SerialCodec.Encode(SendInput.Text, Selected<SerialTextMode>(SendMode), Selected<SerialLineEnding>(EndingPicker), EscapeCheck.IsChecked == true);
            await service.SendAsync(bytes);
            var text = SendInput.Text; history.Remove(text); history.Insert(0, text); if (history.Count > 30) history.RemoveRange(30, history.Count - 30); UpdateHistory();
            Notice.Text = $"已提交发送 {bytes.Length} B"; if (!repeat.IsEnabled) await SavePreferencesAsync();
        }
        catch (Exception ex) { StopRepeat(); ShowError(ex); }
        finally { sending = false; await PaintAsync(); }
    }
    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();
    private void AnalyzeOffline_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            service?.AnalyzeOffline(SerialCodec.Encode(SendInput.Text, Selected<SerialTextMode>(SendMode), Selected<SerialLineEnding>(EndingPicker), EscapeCheck.IsChecked == true));
            ReceiveTabs.SelectedIndex = 1; Notice.Text = "已送入离线 RX 解析，未向串口发送。";
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void AppendCrc_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var bytes = SerialCodec.Encode(SendInput.Text, SerialTextMode.Hex, SerialLineEnding.None, false);
            if (bytes.Length >= 4 && ModbusRtuDecoder.Crc(bytes.AsSpan(0, bytes.Length - 2)) == (bytes[^2] | bytes[^1] << 8))
            { Notice.Text = "报文已包含有效 CRC，未重复追加。"; return; }
            SendInput.Text = string.Join(' ', ModbusRtuDecoder.WithCrc(bytes).Select(b => b.ToString("X2")));
            SendMode.SelectedValue = SerialTextMode.Hex; EndingPicker.SelectedValue = SerialLineEnding.None;
            Notice.Text = "已补全 CRC 并选择 HEX / 无行尾，尚未发送。";
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void SendInput_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; await SendAsync(); } }
    private void UpdateHistory() { HistoryPicker.ItemsSource = history.Select(t => new Choice<string>(t, t.Replace("\r", "").Replace("\n", " ↵ "))).ToArray(); HistoryPicker.DisplayMemberPath = "Label"; }
    private void History_Changed(object sender, SelectionChangedEventArgs e) { if (HistoryPicker.SelectedItem is Choice<string> choice) SendInput.Text = choice.Value; }
    private void Repeat_Click(object sender, RoutedEventArgs e)
    {
        if (RepeatCheck.IsChecked != true) { StopRepeat(); return; }
        if (!int.TryParse(IntervalInput.Text, out var ms) || ms is < 20 or > 3600000) { StopRepeat(); ShowError(new ArgumentException("定时周期应为 20–3600000 ms。")); return; }
        repeat.Interval = TimeSpan.FromMilliseconds(ms); repeat.Start(); IntervalInput.IsEnabled = false;
    }
    private void StopRepeat() { repeat.Stop(); RepeatCheck.IsChecked = false; IntervalInput.IsEnabled = true; }
    private async void Pause_Click(object sender, RoutedEventArgs e) { paused = !paused; PauseButton.Content = paused ? "恢复显示" : "暂停显示"; if (!paused) { renderedVersion = -1; await PaintAsync(); } }
    private async void Clear_Click(object sender, RoutedEventArgs e) { service?.Clear(); renderedVersion = -1; ReceiveEditor.Clear(); Notice.Text = "已清空显示与历史缓存，字节计数保留。"; await PaintAsync(); }
    private async void Counters_Click(object sender, RoutedEventArgs e) { service?.ResetCounters(); await PaintAsync(); }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = "serial-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), Filter = "文本日志（UTF-8）|*.txt|原始接收字节|*.bin|收发记录（JSON Lines）|*.jsonl", AddExtension = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true || service is null) return;
        try { await service.ExportAsync(dialog.FileName); Notice.Text = "已导出：" + dialog.FileName; } catch (Exception ex) { ShowError(ex); }
    }
    private void ShowError(Exception ex) { Notice.Text = ex.GetBaseException().Message; Notice.ToolTip = ex.ToString(); }
    public async Task CloseSessionAsync() { StopRepeat(); if (service is not null) await service.DisconnectAsync(); await SavePreferencesAsync(); }
    public async Task ShutdownAsync() { shuttingDown = true; refresh.Stop(); StopRepeat(); await displayTask; await CloseSessionAsync(); await preferenceTask; }

    internal sealed class TerminalColorizer : DocumentColorizingTransformer
    {
        public IReadOnlyList<TerminalSpan> Spans { get; set; } = [];
        private readonly Dictionary<int, Brush> brushes = [];
        private Brush ColorBrush(int rgb) { if (brushes.TryGetValue(rgb, out var brush)) return brush; var created = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)); created.Freeze(); if (brushes.Count > 2048) brushes.Clear(); return brushes[rgb] = created; }
        protected override void ColorizeLine(DocumentLine line)
        {
            // 按有序绝对偏移二分定位，只处理当前可见行的颜色片段。
            var low = 0; var high = Spans.Count;
            while (low < high) { var mid = (low + high) / 2; if (Spans[mid].Start + Spans[mid].Length <= line.Offset) low = mid + 1; else high = mid; }
            for (var i = low; i < Spans.Count && Spans[i].Start < line.EndOffset; i++)
            {
                var span = Spans[i]; var start = Math.Max(line.Offset, span.Start); var end = Math.Min(line.EndOffset, span.Start + span.Length); if (end <= start) continue;
                ChangeLinePart(start, end, element =>
                {
                    var s = span.Style; var fg = s.Inverse ? s.Background ?? 0x1E1F22 : s.Foreground; var bg = s.Inverse ? s.Foreground ?? 0xDFE1E5 : s.Background;
                    if (fg is { } f) element.TextRunProperties.SetForegroundBrush(ColorBrush(f));
                    if (bg is { } b) element.TextRunProperties.SetBackgroundBrush(ColorBrush(b));
                    var face = element.TextRunProperties.Typeface;
                    if (s.Bold || s.Italic) element.TextRunProperties.SetTypeface(new Typeface(face.FontFamily, s.Italic ? FontStyles.Italic : face.Style, s.Bold ? FontWeights.Bold : face.Weight, face.Stretch));
                    if (s.Underline) element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                });
            }
        }
    }
}
