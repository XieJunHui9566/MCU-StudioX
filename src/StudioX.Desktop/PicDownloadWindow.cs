namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Engine.Pic;

/// <summary>明确选择可用 PICkit3；刷新只枚举 USB，不连接目标。</summary>
public sealed class PicDownloadWindow : Window
{
    private readonly ComboBox programmer = new() { DisplayMemberPath = nameof(PicProgrammer.DisplayName), MinWidth = 360 };
    private readonly CheckBox release = new() { Content = "校验后释放复位，运行程序", Margin = new(0, 16, 0, 12) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 12) };
    public string Serial => ((PicProgrammer)programmer.SelectedItem).Serial;
    public bool Release => release.IsChecked == true;

    public PicDownloadWindow(PicDownloadSettings settings, PicProgrammer[] programmers, Func<Task<PicProgrammer[]>> refresh)
    {
        Title = "PICkit3 下载设置";
        Width = 680;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface");
        SetResourceReference(ForegroundProperty, "Text");
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock { Text = "PIC16F877A · ICSP\n目标板使用独立电源；连接 MCLR/VPP、VDD 检测、GND、PGD/RB7、PGC/RB6。\n请核对接口方向和目标供电。下载组件由 MCU StudioX 管理，无需打开其他软件。", TextWrapping = TextWrapping.Wrap });
        var row = new DockPanel { Margin = new(0, 18, 0, 0) };
        var button = new Button { Content = "刷新烧录器", Margin = new(12, 0, 0, 0) };
        DockPanel.SetDock(button, Dock.Right);
        row.Children.Add(button);
        row.Children.Add(programmer);
        panel.Children.Add(row);
        void Restore(PicProgrammer[] values, string selected)
        {
            programmer.ItemsSource = values;
            programmer.SelectedItem = values.SingleOrDefault(item => item.Serial == selected);
            status.Text = values.Length == 0 ? "未找到 PICkit3。请连接 USB 后刷新；Windows 会自动绑定通用 USB 驱动。" : "请选择本次使用的 PICkit3。";
        }
        Restore(programmers, settings.ProgrammerSerial);
        release.IsChecked = settings.ReleaseAfterProgramming;
        button.Click += async (_, _) =>
        {
            var selected = (programmer.SelectedItem as PicProgrammer)?.Serial ?? settings.ProgrammerSerial;
            button.IsEnabled = false;
            try
            {
                Restore(await refresh(), selected);
            }
            catch (Exception failure) { status.Text = failure.ToString(); }
            finally { button.IsEnabled = true; }
        };
        panel.Children.Add(release);
        panel.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new(0, 0, 12, 0) });
        var save = new Button { Content = "保存设置", IsDefault = true };
        save.Click += (_, _) =>
        {
            if (programmer.SelectedItem is not PicProgrammer)
            {
                status.Text = "请明确选择烧录器序列号。";
                return;
            }
            DialogResult = true;
        };
        actions.Children.Add(save);
        panel.Children.Add(actions);
        Content = panel;
    }
}
