namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Engine.Arduino;

/// <summary>收集明确的 sketch 名称与板卡；工程创建由应用服务执行。</summary>
public sealed class ArduinoProjectWindow : Window
{
    private readonly TextBox name = new() { Text = "Arduino_Demo", Margin = new(0, 5, 0, 16) };
    private readonly ComboBox boards = new() { DisplayMemberPath = "DisplayName", Margin = new(0, 5, 0, 20) };

    public string ProjectName => name.Text.Trim();
    public string BoardId => ((ArduinoBoard)boards.SelectedItem).Id;

    public ArduinoProjectWindow(IReadOnlyList<ArduinoBoard> supported, string? importedName = null)
    {
        Title = importedName is null ? "新建 Arduino 工程" : "导入 Arduino sketch 副本";
        Width = 620;
        Height = 350;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface");
        SetResourceReference(ForegroundProperty, "Text");
        boards.ItemsSource = supported;
        boards.SelectedIndex = -1;
        name.Text = importedName ?? "Arduino_Demo";
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock { Text = "工程名称（英文、数字、下划线）" });
        panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "目标开发板" });
        panel.Children.Add(boards);
        panel.Children.Add(new TextBlock
        {
            Text = "导入 sketch 副本；请明确选择原程序对应的开发板配置。\n新建工程请在统一新建页选择器件及 Arduino 起始模板。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 0, 0, 16)
        });
        var accept = new Button { Content = "选择保存位置…", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        accept.IsEnabled = false;
        boards.SelectionChanged += (_, _) => accept.IsEnabled = boards.SelectedItem is ArduinoBoard;
        accept.Click += (_, _) => DialogResult = true;
        panel.Children.Add(accept);
        Content = panel;
    }
}
