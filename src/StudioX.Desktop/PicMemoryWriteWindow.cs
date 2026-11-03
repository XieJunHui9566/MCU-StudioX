namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;

/// <summary>输入明确的文件寄存器地址与字节；不提供程序区、配置位或 EEPROM 写入。</summary>
public sealed class PicMemoryWriteWindow : Window
{
    public uint Address
    {
        get; private set;
    }
    public byte[] Bytes { get; private set; } = [];
    public PicMemoryWriteWindow(string initialAddress)
    {
        Title = "PIC 文件寄存器写入";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        SetResourceReference(BackgroundProperty, "Surface");
        SetResourceReference(ForegroundProperty, "Text");
        var panel = new StackPanel { Margin = new(22) };
        panel.Children.Add(new TextBlock { Text = "暂停时写入文件寄存器，成功后回读核对。SFR 写入会影响外设状态。", TextWrapping = TextWrapping.Wrap });
        var address = new TextBox { Text = initialAddress, Margin = new(0, 12, 0, 8), ToolTip = "文件寄存器十六进制地址" };
        var bytes = new TextBox { Text = "00", Margin = new(0, 0, 0, 8), ToolTip = "1–64 个十六进制字节，例如 01 02 FF" };
        panel.Children.Add(new TextBlock { Text = "文件寄存器地址（十六进制）", Margin = new(0, 12, 0, 0) });
        System.Windows.Automation.AutomationProperties.SetName(address, "文件寄存器地址（十六进制）");
        System.Windows.Automation.AutomationProperties.SetName(bytes, "写入字节（1–64 个）");
        panel.Children.Add(address);
        panel.Children.Add(new TextBlock { Text = "写入字节（1–64 个）", Margin = new(0, 0, 0, 6) });
        panel.Children.Add(bytes);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) };
        error.SetResourceReference(TextBlock.ForegroundProperty, "DiagnosticError");
        panel.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new(0, 0, 12, 0) };
        actions.Children.Add(cancel);
        var write = new Button { Content = "写入并回读", IsDefault = true };
        actions.Children.Add(write);
        write.Click += (_, _) =>
        {
            try
            {
                Address = uint.Parse(address.Text.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                Bytes = Convert.FromHexString(string.Concat(bytes.Text.Where(c => !char.IsWhiteSpace(c))));
                if (Bytes.Length is < 1 or > 64)
                {
                    throw new FormatException("请输入 1–64 个十六进制字节。");
                }
                DialogResult = true;
            }
            catch (Exception failure) when (failure is FormatException or OverflowException) { error.Text = failure.Message; }
        };
        panel.Children.Add(actions);
        Content = panel;
    }
}
