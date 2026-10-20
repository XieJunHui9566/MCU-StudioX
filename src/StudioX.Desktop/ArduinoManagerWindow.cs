namespace StudioX.Desktop;

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using StudioX.Application.Arduino;
using StudioX.Engine;
using StudioX.Engine.Arduino;

/// <summary>呈现锁定的 Arduino 库和索引搜索；窗口关闭会取消在途任务并等待工具退出。</summary>
public sealed class ArduinoManagerWindow : Window
{
    private readonly ArduinoService service;
    private readonly string root;
    private readonly TextBox name = new() { Text = "Adafruit AHTX0", Width = 280, Margin = new(0, 0, 8, 0) };
    private readonly TextBox version = new() { Width = 120, Margin = new(0, 0, 8, 0) };
    private readonly TextBox output = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ListBox libraries = new() { Height = 110 };
    private readonly ListBox results = new() { Height = 140, DisplayMemberPath = "Name" };
    private readonly List<Button> actions = [];
    private CancellationTokenSource? operation;
    private bool closeRequested;

    public ArduinoManagerWindow(ArduinoService service, string root, ProjectManifest project)
    {
        this.service = service;
        this.root = root;
        Title = "Arduino 工程与库管理";
        Width = 850;
        Height = 700;
        MinWidth = 720;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface");
        SetResourceReference(ForegroundProperty, "Text");
        foreach (var list in new[] { libraries, results })
        {
            list.SetResourceReference(BackgroundProperty, "Panel");
            list.SetResourceReference(ForegroundProperty, "Text");
            list.SetResourceReference(BorderBrushProperty, "Border");
        }
        var layout = new DockPanel { Margin = new(22) };
        var panel = new StackPanel();
        DockPanel.SetDock(panel, Dock.Top);
        panel.Children.Add(new TextBlock { Text = ArduinoBoard.Require(project.Arduino!.BoardId).DisplayName, FontSize = 20 });
        panel.Children.Add(new TextBlock
        {
            Text = project.Arduino!.Platform.DisplayName + " / CLI " + ArduinoPlatform.CliVersion + "\n顶部“编译”构建当前 sketch；顶部“下载”选择串口并确认固件。",
            Margin = new(0, 8, 0, 16)
        });
        panel.Children.Add(new TextBlock { Text = "工程锁定的库（含依赖）" });
        panel.Children.Add(libraries);
        RefreshLibraries(project.Arduino.Libraries);
        var restore = ActionButton("恢复工程锁定的库", () => service.RestoreLibrariesAsync(root, Progress(), operation!.Token));
        restore.HorizontalAlignment = HorizontalAlignment.Left;
        restore.Margin = new(0, 8, 0, 16);
        panel.Children.Add(restore);
        panel.Children.Add(new TextBlock { Text = "库名称 / 精确版本（搜索后选择条目，也可手动填写旧版本）", Margin = new(0, 0, 0, 6) });
        var inputs = new WrapPanel();
        inputs.Children.Add(name);
        inputs.Children.Add(version);
        inputs.Children.Add(ActionButton("搜索", async () =>
        {
            results.ItemsSource = await service.SearchLibrariesAsync(root, name.Text.Trim(), operation!.Token);
            output.Text = "选择搜索结果后安装；结果展示最新版本，可手动填写需要的精确版本。";
        }));
        inputs.Children.Add(ActionButton("安装并锁定", async () =>
        {
            var installed = await service.InstallLibraryAsync(root, name.Text.Trim(), version.Text.Trim(), Progress(), operation!.Token);
            RefreshLibraries(installed);
            output.AppendText("\n已锁定库及传递依赖的精确版本。请重新编译工程。\n");
        }));
        panel.Children.Add(inputs);
        results.Margin = new(0, 10, 0, 12);
        results.SelectionChanged += (_, _) =>
        {
            if (results.SelectedItem is ArduinoLibrarySearchResult selected)
            {
                name.Text = selected.Name;
                version.Text = selected.Version;
                output.Text = selected.Description;
            }
        };
        panel.Children.Add(results);
        layout.Children.Add(panel);
        layout.Children.Add(output);
        Content = layout;
        Closing += HandleClosing;
    }

    private IProgress<string> Progress() => new Progress<string>(text =>
    {
        if (output.Text.Length > 250000)
        {
            output.Text = output.Text[^125000..];
        }
        output.AppendText(text);
        output.ScrollToEnd();
    });

    private void RefreshLibraries(ArduinoLibrary[] items) => libraries.ItemsSource = items.Length == 0
        ? new[] { "未添加外部库；Wire、SPI、EEPROM 等随固定 AVR 核心提供。" }
        : items.Select(item => item.Name + " @ " + item.Version).ToArray();

    private Button ActionButton(string text, Func<Task> action)
    {
        var button = new Button { Content = text, Margin = new(0, 0, 8, 0) };
        actions.Add(button);
        button.Click += async (_, _) =>
        {
            operation = new();
            foreach (var item in actions)
            {
                item.IsEnabled = false;
            }
            try
            {
                output.Text = "正在处理…\n";
                await action();
            }
            catch (OperationCanceledException) { output.AppendText("\n操作已取消。\n"); }
            catch (Exception error) { output.AppendText("\n" + error + "\n"); }
            finally
            {
                operation.Dispose();
                operation = null;
                foreach (var item in actions)
                {
                    item.IsEnabled = true;
                }
                if (closeRequested)
                {
                    Close();
                }
            }
        };
        return button;
    }

    private void HandleClosing(object? sender, CancelEventArgs args)
    {
        if (operation is not null)
        {
            args.Cancel = true;
            closeRequested = true;
            operation.Cancel();
            output.AppendText("\n正在停止工具，结束后关闭窗口…\n");
        }
    }
}
