namespace StudioX.PortableDesktop;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using StudioX.Application;

internal sealed partial class MainWindow
{
    private void CreateWelcome()
    {
        var page = new StackPanel { Spacing = 22, Margin = new Thickness(44, 34) };
        page.Children.Add(Label("MCU StudioX", 38, weight: FontWeight.SemiBold));
        page.Children.Add(Label("嵌入式开发，从这里开始。", 14, "Muted"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Children.Add(CommandButton("plus", "新建工程", "新建工程", () => RunOperationAsync(NewProjectAsync)));
        actions.Children.Add(CommandButton("folder", "打开工程…", "打开工程", () => RunOperationAsync(PickProjectAsync)));
        actions.Children.Add(CommandButton("package", "导入器件包", "导入 StudioX 格式 1 器件包", () => RunOperationAsync(ImportPackAsync)));
        page.Children.Add(actions);
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("1.15*,40,*"), Margin = new Thickness(0, 14) };
        var recent = new StackPanel { Spacing = 18 };
        recent.Children.Add(Label("最近的工程", 16, weight: FontWeight.SemiBold));
        recent.Children.Add(recentProjects);
        columns.Children.Add(recent);
        var start = new StackPanel { Spacing = 16 };
        start.Children.Add(Label("开始使用", 16, weight: FontWeight.SemiBold));
        start.Children.Add(CommandButton("package", "器件与模板", "选择已安装的器件包创建工程", () => RunOperationAsync(NewProjectAsync)));
        start.Children.Add(CommandButton("image", "让工作台属于你", "深浅主题", () => RunOperationAsync(ShowAppearanceAsync)));
        start.Children.Add(CommandButton("git-branch", "GitHub 协作", "GitHub 协作", null));
        start.Children.Add(CommandButton("serial", "串口终端", "串口终端", null));
        AddAt(columns, start, 0, 2);
        page.Children.Add(columns);
        welcome.Children.Add(new ScrollViewer { Content = page });
    }

    private async Task RefreshWelcomeAsync(CancellationToken token)
    {
        var recent = await workspace.RecentAsync(token);
        recentProjects.Children.Clear();
        if (recent.Count == 0)
        {
            recentProjects.Children.Add(new TextBlock { Text = "你的下一个项目，从这里开始\n\n打开过的工程会保留在这里，\n下次可以直接继续。", Foreground = ColorBrush("Muted"), TextWrapping = TextWrapping.Wrap });
        }
        foreach (var item in recent)
        {
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = ColorBrush("Surface"),
                Foreground = ColorBrush("Text"),
                Padding = new Thickness(14, 10),
                Content = new StackPanel
                {
                    Spacing = 5,
                    Children = { new TextBlock { Text = item.Name }, new TextBlock { Text = item.Directory, FontSize = 11, Foreground = ColorBrush("Muted"), TextTrimming = TextTrimming.CharacterEllipsis } }
                }
            };
            ToolTip.SetTip(button, item.Directory);
            button.Click += (_, _) => RunOperationAsync(async cancellation =>
            {
                if (await ConfirmBuffersAsync(buffers, cancellation))
                {
                    await OpenProjectAsync(item.Directory, cancellation);
                }
            });
            recentProjects.Children.Add(button);
        }
    }

    private void ShowWelcome()
    {
        if (closing)
        {
            return;
        }
        RememberPosition();
        activeBuffer = null;
        welcome.IsVisible = true;
        editor.IsVisible = false;
        UpdateTabSelection();
        UpdateControls();
    }

    private async Task ShowAppearanceAsync(CancellationToken token)
    {
        var dialog = new Window { Title = "外观与背景", Width = 440, Height = 260, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var theme = new ComboBox { ItemsSource = new[] { "StudioX Dark", "StudioX Light" }, SelectedIndex = currentTheme.Id == "studiox.light" ? 1 : 0 };
        var apply = new Button { Content = "应用", HorizontalAlignment = HorizontalAlignment.Right };
        apply.Click += (_, _) => dialog.Close(theme.SelectedIndex);
        dialog.Content = new StackPanel
        {
            Spacing = 20,
            Margin = new Thickness(24),
            Children = { new TextBlock { Text = "主题", FontSize = 18 }, theme,
                new TextBlock { Text = "背景图片与视频功能正在迁移。", Foreground = ColorBrush("Muted") }, apply }
        };
        using var registration = token.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() => dialog.Close(-1)));
        var result = await dialog.ShowDialog<int?>(this);
        if (result is 0 or 1)
        {
            var selected = result == 0 ? ThemeService.Dark : ThemeService.Light;
            await workspace.SelectThemeAsync(selected, token);
            ApplyTheme(selected);
            await RefreshWelcomeAsync(token);
        }
    }

    private async Task ImportPackAsync(CancellationToken token)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入 StudioX 格式 1 器件包",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("StudioX Pack") { Patterns = ["*.mcupack"] }]
        });
        if (files.Count == 1 && files[0].TryGetLocalPath() is { } path)
        {
            await workspace.ImportPackAsync(path, token);
            status.Text = "器件包已导入";
        }
    }

    private async Task ShowAboutAsync(CancellationToken token)
    {
        var dialog = new Window { Title = "关于 MCU StudioX", Width = 500, Height = 230, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var close = new Button { Content = "关闭", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 18,
            Children = { new TextBlock { Text = ProductInfo.DisplayName, FontSize = 22 },
                new TextBlock { Text = "Linux 迁移预览：工程、编辑、编译与主题已接入。\n调试、终端、语言服务及扩展界面仍在迁移。", TextWrapping = TextWrapping.Wrap }, close }
        };
        using var registration = token.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(dialog.Close));
        await dialog.ShowDialog(this);
    }
}
