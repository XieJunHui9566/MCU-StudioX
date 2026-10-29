namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Application;

/// <summary>仅在设备选择不唯一或已绑定设备缺席时显示，不跳转工程设置。</summary>
public sealed class Mx251DownloadDeviceWindow : Window
{
    public string? DevicePath
    {
        get; private set;
    }
    public Mx251DownloadDeviceWindow(Mx251DownloadDeviceChoice choice)
    {
        Title = "选择 USB 下载器";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "ChromeSurface");
        SetResourceReference(ForegroundProperty, "Text");
        var body = new StackPanel { Margin = new(20) };
        body.Children.Add(new TextBlock { Text = choice.Message, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
        var picker = new ComboBox
        {
            MinHeight = 36,
            SelectedIndex = -1,
            DisplayMemberPath = "Label",
            SelectedValuePath = "Path",
            ItemsSource = choice.Devices.Select((d, i) => new { d.Path, Label = $"{d.Product} · USB 下载器 {i + 1}" }).ToArray()
        };
        body.Children.Add(picker);
        var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 12) };
        body.Children.Add(detail);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var download = new Button { Content = "开始下载", IsDefault = true, IsEnabled = false };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new(10, 0, 0, 0) };
        picker.SelectionChanged += (_, _) => { download.IsEnabled = picker.SelectedValue is string; detail.Text = picker.SelectedValue as string ?? ""; };
        download.Click += (_, _) => { DevicePath = picker.SelectedValue as string; DialogResult = true; };
        row.Children.Add(download);
        row.Children.Add(cancel);
        body.Children.Add(row);
        Content = body;
    }
}
