namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Engine.Arduino;

/// <summary>展示当前 HEX 身份并收集用户明确选择的串口；本窗口不访问设备。</summary>
public sealed class ArduinoUploadWindow : Window
{
    private readonly TextBox port = new() { Margin = new(0, 8, 0, 10) };
    public string Port => port.Text.Trim();

    public ArduinoUploadWindow(ArduinoUploadPreview preview)
    {
        Title = "确认 Arduino 上传";
        Width = 700;
        Height = 400;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface");
        SetResourceReference(ForegroundProperty, "Text");
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock
        {
            Text = $"{preview.Board.DisplayName}\n{preview.Board.Fqbn}\n\n固件：{Path.GetFileName(preview.HexPath)} · {preview.Bytes:N0} 字节\nSHA-256：{preview.Sha256}\n\n填写板卡的 USB 串口，例如 COM3。上传会替换板上程序。" +
                (preview.Board.PlatformId == "esp32" ? "\nESP32 将同时写入本次构建配套的分区表和 bootloader；这些镜像已纳入完整性确认。" : ""),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(port);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) };
        panel.Children.Add(error);
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
        panel.Children.Add(upload);
        Content = panel;
    }
}
