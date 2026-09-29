namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;

internal sealed class SymbolRenameDialog : Window
{
    private readonly TextBox name;
    public string NewName => name.Text.Trim();
    public SymbolRenameDialog(string oldName)
    {
        Title = "重命名 C/C++ 符号";
        Width = 430;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface");
        SetResourceReference(ForegroundProperty, "Text");
        var panel = new StackPanel { Margin = new(20) };
        panel.Children.Add(new TextBlock { Text = "新名称（下一步预览所有引用的修改）", Margin = new(0, 0, 0, 12) });
        name = new TextBox { Text = oldName, Margin = new(0, 0, 0, 16) };
        panel.Children.Add(name);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new(0, 0, 8, 0) };
        var preview = new Button { Content = "预览修改", IsDefault = true };
        preview.Click += (_, _) => { if (NewName.Length > 0) { DialogResult = true; } };
        buttons.Children.Add(cancel);
        buttons.Children.Add(preview);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
    }
}
