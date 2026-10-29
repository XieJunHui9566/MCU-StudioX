namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Engine.Arduino;

/// <summary>展示构建身份、恢复工程端口并请求应用层刷新端口；本窗口不打开设备。</summary>
public sealed class ArduinoUploadWindow : Window
{
    private readonly ComboBox port = new() { IsEditable = true, IsTextSearchEnabled = false, MinWidth = 220 };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
    private readonly Func<Task<string[]>> listPorts;
    public string Port => port.Text.Trim();

    public ArduinoUploadWindow(ArduinoUploadPreview preview, string[]? ports = null, string selectedPort = "",
        Func<Task<string[]>>? listPorts = null)
    {
        this.listPorts = listPorts ?? (() => Task.FromResult(ports ?? []));
        Title = "确认 Arduino 上传";
        Width = 740;
        SizeToContent = SizeToContent.Height;
        MinWidth = 660;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface");
        SetResourceReference(ForegroundProperty, "Text");
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock
        {
            Text = $"{preview.Board.DisplayName}\n{preview.Board.Fqbn}\n\n固件：{Path.GetFileName(preview.HexPath)}\n文件大小：{preview.Bytes:N0} 字节" +
                (preview.Board.PlatformId == "avr" ? "（HEX 文本大小；Flash 占用见编译报告）" : "") +
                $"\nSHA-256：{preview.Sha256}\n\n选择板卡的 USB 串口。上传会替换板上程序。\n板型或 Nano 引导程序不符时，请先到 Arduino 工程与库管理中修改，再重新编译。" +
                (preview.Board.PlatformId == "esp32" ? "\nESP32 将同时写入本次构建配套的分区表和 bootloader；这些镜像已纳入完整性确认。" : ""),
            TextWrapping = TextWrapping.Wrap
        });
        var inputs = new DockPanel { Margin = new(0, 12, 0, 0) };
        var refresh = new Button { Content = "刷新串口", Margin = new(10, 0, 0, 0) };
        DockPanel.SetDock(refresh, Dock.Right);
        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = false;
            try
            {
                await RefreshPortsAsync();
                error.Text = "";
            }
            catch (Exception failure) { error.Text = failure.ToString(); }
            finally { refresh.IsEnabled = true; }
        };
        inputs.Children.Add(refresh);
        port.SetResourceReference(BackgroundProperty, "Panel");
        port.SetResourceReference(ForegroundProperty, "Text");
        inputs.Children.Add(port);
        panel.Children.Add(inputs);
        RestorePorts(ports ?? [], selectedPort);
        panel.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new(0, 0, 10, 0) });
        var upload = new Button { Content = "确认上传", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        upload.Click += (_, _) =>
        {
            try
            {
                _ = ArduinoBuildService.ValidatePort(Port);
                DialogResult = true;
            }
            catch (Exception failure) { error.Text = failure.Message; }
        };
        actions.Children.Add(upload);
        panel.Children.Add(actions);
        Content = panel;
    }

    internal async Task RefreshPortsAsync()
    {
        var ports = await listPorts();
        // 等待枚举时仍允许编辑；保留完成时的输入，端口消失也不能自动改选另一台设备。
        RestorePorts(ports, Port);
    }

    private void RestorePorts(string[] ports, string selected)
    {
        port.ItemsSource = ports;
        var matching = ports.FirstOrDefault(item => item.Equals(selected, StringComparison.OrdinalIgnoreCase));
        port.SelectedItem = matching;
        port.Text = matching ?? selected;
    }
}
