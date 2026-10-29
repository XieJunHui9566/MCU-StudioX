namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Engine.Pic;

/// <summary>将精确固件和配置字呈交用户；确认前不连接目标。</summary>
public sealed class PicDownloadConfirmWindow : Window
{
    public PicDownloadConfirmWindow(PicDownloadPreview preview)
    {
        Title = "确认 PICkit3 下载";
        Width = 740;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface");
        SetResourceReference(ForegroundProperty, "Text");
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock
        {
            Text = $"目标：{preview.Firmware.DeviceId}\nPICkit3 序列号：{preview.Settings.ProgrammerSerial}\n目标板：独立供电\n\n固件：{Path.GetFileName(preview.Firmware.HexPath)}\n程序：{preview.Image.ProgramWords:N0} 个 14 位字\n配置字：0x{preview.Image.ConfigurationWord:X4}\nSHA-256：{preview.Firmware.Sha256}\n\n将先核对器件 ID 并回读备份，然后替换程序与配置并校验。\n保留 EEPROM 和用户 ID；检测到代码保护时停止。\n" + (preview.Settings.ReleaseAfterProgramming ? "校验后运行程序。" : "校验后保持复位。"),
            TextWrapping = TextWrapping.Wrap
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) };
        actions.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new(0, 0, 12, 0) });
        var download = new Button { Content = "确认下载", IsDefault = true };
        download.Click += (_, _) => DialogResult = true;
        actions.Children.Add(download);
        panel.Children.Add(actions);
        Content = panel;
    }
}
