namespace StudioX.Desktop;

using System.Text;
using System.Windows;
using StudioX.Engine;

/// <summary>显示实际构建布局，用户确认后才能启动下载进程。</summary>
public partial class EspressifFlashReviewWindow : Window
{
    public EspressifFlashReviewWindow(EspressifFlashPreview preview)
    {
        InitializeComponent();
        var layout = preview.Layout;
        var settings = preview.Configuration.Settings;
        TargetLabel.Text = $"{preview.Configuration.Device.Id} / {layout.Target}\n{settings.Port} · {settings.BaudRate} baud\n" +
            $"Flash {layout.FlashSize} · {layout.FlashMode} · {layout.FlashFrequency}";
        var text = new StringBuilder($"布局 SHA-256：\n{layout.LayoutSha256}\n\n");
        foreach (var image in layout.Images)
        {
            text.AppendLine($"{image.RelativePath}\n地址 0x{image.Offset:x8} · {image.Bytes:N0} 字节\nSHA-256 {image.Sha256}\n");
        }
        text.AppendLine("将覆盖这些映像所在的 Flash 擦除扇区，逐个校验并复位运行。");
        text.AppendLine(layout.UseStub ? "SDK 配置启用了临时 RAM 下载器。" : "使用芯片 ROM 下载器。");
        LayoutDetails.Text = text.ToString();
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
