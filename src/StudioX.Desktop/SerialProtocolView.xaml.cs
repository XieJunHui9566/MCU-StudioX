namespace StudioX.Desktop;

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StudioX.Application.Serial;

public partial class SerialProtocolView : UserControl
{
    private sealed record Choice<T>(T Value, string Label);
    private SerialTerminalService? service;
    private string? scriptPath;
    private long version = -1;
    private readonly ObservableCollection<ProtocolFrame> rows = [];
    private bool ready, changing;
    public SerialProtocolView()
    {
        InitializeComponent();
        DecoderPicker.ItemsSource = new[] { new Choice<SerialProtocol>(SerialProtocol.None, "关闭解析"), new(SerialProtocol.ModbusRtu, "Modbus RTU"), new(SerialProtocol.JavaScript, "自定义 JavaScript") };
        DecoderPicker.SelectedIndex = 0;
        RolePicker.ItemsSource = new[] { new Choice<ModbusRole>(ModbusRole.PcMaster, "电脑为主站 · TX 请求"), new(ModbusRole.PcSlave, "电脑为从站 · RX 请求"), new(ModbusRole.Monitor, "被动监听 · 推断方向") };
        RolePicker.SelectedIndex = 0; FrameGrid.ItemsSource = rows; ready = true;
    }
    public void Attach(SerialTerminalService value) => service = value;
    public void RefreshFrames()
    {
        if (!ready || service is null || !IsVisible) return;
        var snapshot = service.Protocol.Snapshot(version); if (snapshot is null) return;
        version = snapshot.Version;
        var direction = (DirectionPicker.SelectedItem as ComboBoxItem)?.Content as string;
        var search = SearchInput.Text.Trim();
        var filtered = snapshot.Frames.Where(f => (direction == "全部" || f.Direction == direction) &&
            (ErrorsOnly.IsChecked != true || f.Status is not ("CRC 正确" or "已解析")) &&
            (search.Length == 0 || (f.Address + " " + f.Function + " " + f.Summary + " " + f.Hex).Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        var byId = filtered.ToDictionary(f => f.Sequence);
        for (var i = rows.Count - 1; i >= 0; i--)
            if (!byId.TryGetValue(rows[i].Sequence, out var current) || !ReferenceEquals(current, rows[i])) rows.RemoveAt(i);
        var existing = rows.Select(f => f.Sequence).ToHashSet();
        foreach (var f in filtered) if (existing.Add(f.Sequence)) rows.Add(f);
        if (FollowCheck.IsChecked == true && rows.Count > 0) FrameGrid.ScrollIntoView(rows[^1]);
        StatusText.Text = $"{snapshot.Frames.Length} 帧 · 显示 {rows.Count} · 淘汰 {snapshot.Discarded} · 解析丢帧 {snapshot.Dropped}  |  " + snapshot.Message.Split('\n')[0];
        StatusText.ToolTip = snapshot.Message;
    }
    private void Invalidate() { version = -1; rows.Clear(); RefreshFrames(); }
    private void Decoder_Changed(object sender, SelectionChangedEventArgs e)
    { if (ready) RolePicker.IsEnabled = DecoderPicker.SelectedValue is SerialProtocol.ModbusRtu; }
    private async Task ApplyAsync()
    {
        if (service is null || changing) return;
        if (!int.TryParse(IdleInput.Text, out var idle)) throw new ArgumentException("请输入空闲收尾毫秒数。");
        changing = true; ApplyButton.IsEnabled = false;
        try
        {
            await service.Protocol.ConfigureAsync(new((SerialProtocol)DecoderPicker.SelectedValue, (ModbusRole)RolePicker.SelectedValue, idle, scriptPath));
            ScriptLabel.Text = service.Protocol.Options.Protocol == SerialProtocol.JavaScript ? System.IO.Path.GetFileName(scriptPath) : "";
            rows.Clear(); DetailText.Text = "选择一帧查看字段、原始字节和校验结果。"; version = -1; RefreshFrames();
        }
        finally { changing = false; ApplyButton.IsEnabled = true; }
    }
    private async void Apply_Click(object sender, RoutedEventArgs e) { try { await ApplyAsync(); } catch (Exception ex) { Error(ex); } }
    private async void LoadScript_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "JavaScript 协议解析脚本|*.js", Title = "加载串口协议脚本" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        scriptPath = dialog.FileName; ScriptLabel.Text = System.IO.Path.GetFileName(scriptPath); ScriptLabel.ToolTip = scriptPath;
        DecoderPicker.SelectedValue = SerialProtocol.JavaScript;
        try { await ApplyAsync(); } catch (Exception ex) { Error(ex); }
    }
    private async void SaveExample_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = "sensor-frame.js", Filter = "JavaScript 协议解析脚本|*.js" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var source = System.IO.Path.Combine(AppContext.BaseDirectory, "examples", "protocols", "sensor-frame.js");
            var content = await System.IO.File.ReadAllTextAsync(source);
            await System.IO.File.WriteAllTextAsync(dialog.FileName, content);
            StatusText.Text = "示例已保存。修改后点击加载脚本；更新脚本后点击应用 / 重载。";
        }
        catch (Exception ex) { Error(ex); }
    }
    private async void Demo_Click(object sender, RoutedEventArgs e)
    {
        if (service is null || changing) return;
        if (service.Status.Connected) { StatusText.Text = "请先断开串口，再查看离线示例。示例不会发送到设备。"; return; }
        try
        {
            DecoderPicker.SelectedValue = SerialProtocol.ModbusRtu; RolePicker.SelectedValue = ModbusRole.PcMaster;
            await ApplyAsync();
            var now = DateTimeOffset.UtcNow;
            service.Protocol.Observe(new(now, true, Convert.FromHexString("010300000002C40B"), Offline: true));
            service.Protocol.Observe(new(now.AddMilliseconds(1), false, ModbusRtuDecoder.WithCrc([1, 3, 4, 0, 42, 0x12, 0x34]), Offline: true));
            service.Protocol.Observe(new(now.AddMilliseconds(2), false, ModbusRtuDecoder.WithCrc([1, 0x83, 2]), Offline: true));
            service.Protocol.Observe(new(now.AddMilliseconds(3), false, [1, 3, 2, 0, 1, 0, 0], Offline: true));
            service.Protocol.Observe(new(now.AddMilliseconds(4), false, [], true));
            ScriptLabel.Text = "离线示例 · 未发送到设备";
            await Task.Delay(100); RefreshFrames();
        }
        catch (Exception ex) { Error(ex); }
    }
    private void Frame_Changed(object sender, SelectionChangedEventArgs e)
    { DetailText.Text = FrameGrid.SelectedItem is ProtocolFrame f ? f.Summary + Environment.NewLine + f.Status + Environment.NewLine + Environment.NewLine + f.Detail : "选择一帧查看字段、原始字节和校验结果。"; }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (ready) Invalidate(); }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (ready) Invalidate(); }
    private void Filter_Click(object sender, RoutedEventArgs e) => Invalidate();
    private void Clear_Click(object sender, RoutedEventArgs e) { service?.Protocol.Clear(); DetailText.Clear(); Invalidate(); }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (service is null) return;
        var dialog = new SaveFileDialog { FileName = "protocol-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".jsonl", Filter = "解析帧（JSON Lines，保留原始 HEX）|*.jsonl" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { await service.Protocol.ExportAsync(dialog.FileName); StatusText.Text = "已导出全部保留的解析帧：" + dialog.FileName; } catch (Exception ex) { Error(ex); }
    }
    private void Error(Exception ex)
    {
        if (service is not null)
        {
            DecoderPicker.SelectedValue = service.Protocol.Options.Protocol;
            RolePicker.SelectedValue = service.Protocol.Options.Role;
            ScriptLabel.Text = service.Protocol.Options.Protocol == SerialProtocol.JavaScript ? System.IO.Path.GetFileName(service.Protocol.Options.ScriptPath) : "";
        }
        StatusText.Text = "操作失败，保留当前解析器：" + ex.Message.Split('\n')[0]; StatusText.ToolTip = ex.ToString();
    }
}
