namespace StudioX.PortableDesktop;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using StudioX.Application;

internal sealed partial class MainWindow
{
    private ThemeDefinition currentTheme = ThemeService.Dark;
    private readonly List<Action> themePainters = [];
    private readonly StackPanel tabStrip = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel recentProjects = new() { Spacing = 8 };
    private readonly TextBlock projectTarget = new() { Margin = new Thickness(10, 5), MinWidth = 120 };
    private readonly Grid editorRegion = new() { RowDefinitions = new RowDefinitions("34,*,4,210") };
    private readonly Grid body = new() { ColumnDefinitions = new ColumnDefinitions("44,230,4,*,32") };
    private readonly Grid welcome = new();
    private bool outputVisible = true;
    private double outputHeight = 210;
    private bool projectVisible = true;

    private Grid CreateWorkbench()
    {
        // 与 Windows 版保持相同的区域顺序、默认尺寸和语义颜色。
        var layout = new Grid { RowDefinitions = new RowDefinitions("38,30,39,*,26") };
        // 根容器也必须绘制背景，离屏截图与真实窗口才不会出现透明黑底。
        themePainters.Add(() => { layout.Background = ColorBrush("Background"); welcome.Background = ColorBrush("Background"); });
        InstallWorkbenchButtonTheme();
        layout.RowDefinitions[0].Height = new GridLength(WorkbenchVisuals.TitleHeight);
        layout.RowDefinitions[1].Height = new GridLength(WorkbenchVisuals.MenuHeight);
        layout.RowDefinitions[2].Height = new GridLength(WorkbenchVisuals.ToolbarHeight);
        layout.RowDefinitions[4].Height = new GridLength(WorkbenchVisuals.StatusHeight);
        body.ColumnDefinitions[0].Width = new GridLength(WorkbenchVisuals.ActivityWidth);
        body.ColumnDefinitions[1].Width = new GridLength(WorkbenchVisuals.ProjectWidth);
        body.ColumnDefinitions[4].Width = new GridLength(WorkbenchVisuals.UtilityWidth);
        editorRegion.RowDefinitions[0].Height = new GridLength(WorkbenchVisuals.DocumentTabHeight);
        AddAt(layout, CreateTitlebar(), 0);
        AddAt(layout, CreateMenu(), 1);
        AddAt(layout, CreateToolbar(), 2);
        AddAt(layout, body, 3);
        AddAt(layout, CreateStatusbar(), 4);
        var rail = new DockPanel();
        var bottomRail = new StackPanel();
        bottomRail.Children.Add(Rail("terminal", "工程终端", null));
        bottomRail.Children.Add(Rail("build-log", "构建日志 (Alt+4)", () => ShowOutput(!outputVisible)));
        bottomRail.Children.Add(Rail("settings", "外观与背景", () => RunOperationAsync(ShowAppearanceAsync)));
        DockPanel.SetDock(bottomRail, Dock.Bottom);
        rail.Children.Add(bottomRail);
        var topRail = new StackPanel();
        topRail.Children.Add(Rail("folder", "工程 (Alt+1)", ToggleProject));
        topRail.Children.Add(Rail("git-branch", "Git 图谱", null));
        topRail.Children.Add(Rail("chip", "器件与模板", () => RunOperationAsync(NewProjectAsync)));
        topRail.Children.Add(Rail("serial", "串口终端", null));
        topRail.Children.Add(Rail("wave", "串口绘图", null));
        topRail.Children.Add(Rail("plug", "扩展", null));
        rail.Children.Add(topRail);
        AddAt(body, Surface(rail, "Surface", new Thickness(0, 0, 1, 0)), 0, 0);

        var projectPane = new Grid { RowDefinitions = new RowDefinitions("34,34,*,Auto") };
        projectPane.Children.Add(Surface(Label("工程", margin: new Thickness(16, 6)), "Surface", new Thickness(0, 0, 0, 1)));
        var projectHeader = new DockPanel { Margin = new Thickness(12, 0, 5, 0) };
        ConfigureButton(closeProjectButton, "close", "", "关闭工程 (Ctrl+Shift+W)");
        closeProjectButton.Width = 28;
        DockPanel.SetDock(closeProjectButton, Dock.Right);
        projectHeader.Children.Add(closeProjectButton);
        projectHeader.Children.Add(Label("工程", weight: FontWeight.SemiBold));
        AddAt(projectPane, projectHeader, 1);
        tree.Margin = new Thickness(4, 8);
        AddAt(projectPane, tree, 2);
        projectLabel.Margin = new Thickness(12, 12);
        projectLabel.FontSize = 11;
        themePainters.Add(() => projectLabel.Foreground = ColorBrush("Muted"));
        AddAt(projectPane, Surface(projectLabel, "Surface", new Thickness(0, 1, 0, 0)), 3);
        AddAt(body, Surface(projectPane), 0, 1);
        var treeSplitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, HorizontalAlignment = HorizontalAlignment.Stretch };
        AddAt(body, treeSplitter, 0, 2);
        var tabs = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = tabStrip };
        AddAt(editorRegion, Surface(tabs, "Surface", new Thickness(0, 0, 0, 1)), 0);
        AddAt(editorRegion, editor, 1);
        CreateWelcome();
        AddAt(editorRegion, welcome, 1);
        var outputSplitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch };
        AddAt(editorRegion, outputSplitter, 2);
        var output = new Grid { RowDefinitions = new RowDefinitions("34,*") };
        var outputHeader = new DockPanel();
        var hideOutput = Rail("minus", "隐藏输出 (Alt+4)", () => ShowOutput(false));
        hideOutput.Width = 32;
        hideOutput.Height = 28;
        DockPanel.SetDock(hideOutput, Dock.Right);
        outputHeader.Children.Add(hideOutput);
        var outputTabs = new StackPanel { Orientation = Orientation.Horizontal };
        outputTabs.Children.Add(Surface(Label("构建", margin: new Thickness(16, 6)), "Surface", new Thickness(0, 0, 0, 2), "Accent"));
        outputTabs.Children.Add(PendingLabel("设备输出"));
        outputTabs.Children.Add(PendingLabel("终端"));
        outputTabs.Children.Add(PendingLabel("问题"));
        outputHeader.Children.Add(outputTabs);
        output.Children.Add(Surface(outputHeader, "Surface", new Thickness(0, 0, 0, 1)));
        log.BorderThickness = new Thickness(0);
        log.Padding = new Thickness(12, 6);
        AddAt(output, log, 1);
        AddAt(editorRegion, Surface(output), 3);
        AddAt(body, editorRegion, 0, 3);
        var utility = new StackPanel();
        utility.Children.Add(Rail("home", "欢迎", ShowWelcome));
        utility.Children.Add(Rail("image", "外观", () => RunOperationAsync(ShowAppearanceAsync)));
        AddAt(body, Surface(utility, "Surface", new Thickness(1, 0, 0, 0)), 0, 4);
        RebuildTabs();
        return layout;
    }

    private Border CreateTitlebar()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("46,Auto,Auto,*,Auto") };
        var logo = new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://StudioX.PortableDesktop/Assets/StudioX.png"))), Width = 28, Height = 28 };
        AddAt(grid, logo, 0, 0);
        AddAt(grid, Label("MCU StudioX", weight: FontWeight.SemiBold), 0, 1);
        projectTitle.Margin = new Thickness(18, 0, 12, 0);
        projectTitle.VerticalAlignment = VerticalAlignment.Center;
        projectTitle.MaxWidth = 320;
        projectTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        themePainters.Add(() => projectTitle.Foreground = ColorBrush("Muted"));
        AddAt(grid, projectTitle, 0, 2);
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(Rail("ai", "AI 助手", null));
        controls.Children.Add(PendingLabel("未登录 GitHub"));
        controls.Children.Add(Rail("minus", "最小化", () => WindowState = WindowState.Minimized));
        controls.Children.Add(Rail("maximize", "最大化 / 还原", ToggleMaximize));
        controls.Children.Add(Rail("close", "关闭", Close));
        AddAt(grid, controls, 0, 4);
        var border = Surface(grid, "Surface", new Thickness(0, 0, 0, 1));
        border.PointerPressed += (_, e) =>
        {
            if (e.Source is Button || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
            }
            else
            {
                BeginMoveDrag(e);
            }
        };
        return border;
    }

    private Control CreateToolbar()
    {
        var toolbar = new DockPanel { Margin = new Thickness(12, 4) };
        var appearance = CommandButton("settings", "外观", "外观与背景", () => RunOperationAsync(ShowAppearanceAsync));
        DockPanel.SetDock(appearance, Dock.Right);
        toolbar.Children.Add(appearance);
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        commands.Children.Add(CommandButton("plus", "新建", "新建工程 (Ctrl+N)", () => RunOperationAsync(NewProjectAsync)));
        ConfigureButton(openButton, "folder", "打开", "打开工程 (Ctrl+O)");
        ConfigureButton(saveButton, "save", "保存", "保存 (Ctrl+S)");
        ConfigureButton(buildButton, "build", "编译", "保存所有修改并编译 (F7)", "#73B581");
        ConfigureButton(cancelButton, "stop", "停止", "停止当前操作");
        commands.Children.Add(openButton);
        commands.Children.Add(saveButton);
        commands.Children.Add(Surface(projectTarget, "Background", margin: new Thickness(14, 0)));
        commands.Children.Add(buildButton);
        commands.Children.Add(PendingLabel("烧录器"));
        var probe = new ComboBox { Width = 165, Height = 28, IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
        ToolTip.SetTip(probe, "Linux 下载与调试迁移中");
        commands.Children.Add(probe);
        commands.Children.Add(CommandButton("download", "下载", "下载固件", null));
        commands.Children.Add(CommandButton("debug", "调试", "启动调试", null));
        commands.Children.Add(cancelButton);
        toolbar.Children.Add(commands);
        return Surface(toolbar, "Surface", new Thickness(0, 0, 0, 1));
    }

    private Control CreateMenu()
    {
        MenuItem Item(string title, Action? action = null)
        {
            var item = new MenuItem { Header = title, IsEnabled = action is not null };
            if (action is not null)
            {
                item.Click += (_, _) => action();
            }
            else
            {
                ToolTip.SetTip(item, "Linux 功能迁移中");
            }
            return item;
        }
        MenuItem Group(string title, params MenuItem[] items) => new()
        {
            Header = title,
            ItemsSource = items
        };
        var menu = new Menu
        {
            ItemsSource = new[]
            {
                Group("文件(F)", Item("新建工程…   Ctrl+N", () => RunOperationAsync(NewProjectAsync)), Item("打开工程…   Ctrl+O", () => RunOperationAsync(PickProjectAsync)),
                    Item("保存   Ctrl+S", () => RunOperationAsync(SaveDocumentAsync)), Item("全部保存   Ctrl+Shift+S", () => RunOperationAsync(SaveAllAsync)),
                    Item("关闭标签   Ctrl+W", () => RunOperationAsync(CloseActiveAsync)), Item("关闭工程   Ctrl+Shift+W", () => RunOperationAsync(CloseProjectAsync)), Item("退出", Close)),
                Group("编辑(E)", Item("撤销   Ctrl+Z", () => editor.Undo()), Item("重做   Ctrl+Y", () => editor.Redo()), Item("查找   Ctrl+F", () => { searchPanel.Open(); editor.Focus(); })),
                Group("视图(V)", Item("欢迎", ShowWelcome), Item("工程窗口   Alt+1", ToggleProject), Item("底部输出   Alt+4", () => ShowOutput(!outputVisible)), Item("外观与背景…", () => RunOperationAsync(ShowAppearanceAsync))),
                Group("构建(B)", Item("编译   F7", () => RunOperationAsync(BuildProjectAsync)), Item("下载固件…"), Item("停止", () => operationCancellation?.Cancel())),
                Group("调试(D)", Item("启动调试"), Item("附加到设备…"), Item("断点")),
                Group("工具(T)", Item("导入器件包…", () => RunOperationAsync(ImportPackAsync)), Item("开发环境组件"), Item("串口终端"), Item("Git 与协作"), Item("插件与扩展")),
                Group("帮助(H)", Item("关于 MCU StudioX", () => RunOperationAsync(ShowAboutAsync)))
            }
        };
        themePainters.Add(() => menu.Background = ColorBrush("Surface"));
        return menu;
    }

    private Border CreateStatusbar()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(12, 0) };
        grid.Children.Add(status);
        documentLabel.VerticalAlignment = VerticalAlignment.Center;
        AddAt(grid, documentLabel, 0, 1);
        AddAt(grid, Label(ProductInfo.DisplayName, 11, "Muted", margin: new Thickness(24, 0, 0, 0)), 0, 2);
        return Surface(grid, "Surface", new Thickness(0, 1, 0, 0));
    }

    private TextBlock Label(string text, double size = 12, string color = "Text", FontWeight? weight = null, Thickness? margin = null)
    {
        var label = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeight.Normal, Margin = margin ?? new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        themePainters.Add(() => label.Foreground = ColorBrush(color));
        label.Foreground = ColorBrush(color);
        return label;
    }

    private TextBlock PendingLabel(string text)
    {
        var label = Label(text, color: "Muted", margin: new Thickness(12, 6));
        ToolTip.SetTip(label, "Linux 功能迁移中");
        return label;
    }

    private Border Surface(Control child, string color = "Surface", Thickness? border = null, string borderColor = "Border", Thickness? margin = null)
    {
        var surface = new Border { Child = child, BorderThickness = border ?? new Thickness(0), Margin = margin ?? new Thickness(0) };
        themePainters.Add(() => { surface.Background = ColorBrush(color); surface.BorderBrush = ColorBrush(borderColor); });
        surface.Background = ColorBrush(color);
        surface.BorderBrush = ColorBrush(borderColor);
        return surface;
    }

    private Button CommandButton(string icon, string text, string tip, Action? action, string? iconColor = null)
    {
        var button = new Button();
        ConfigureButton(button, icon, text, tip, iconColor);
        button.IsEnabled = action is not null;
        if (action is not null)
        {
            button.Click += (_, _) => action();
        }
        else
        {
            ToolTip.SetTip(button, tip + " · Linux 功能迁移中");
        }
        return button;
    }

    private void ConfigureButton(Button button, string icon, string text, string tip, string? iconColor = null)
    {
        var symbol = new WorkbenchIcon(icon, iconColor is null ? null : Brush.Parse(iconColor)) { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        var contents = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { symbol } };
        if (text.Length > 0)
        {
            contents.Children.Add(Label(text));
        }
        button.Content = contents;
        button.Height = 28;
        button.Padding = new Thickness(8, 3);
        button.BorderThickness = new Thickness(0);
        themePainters.Add(() => { button.Background = ColorBrush("Surface"); button.Foreground = ColorBrush("Text"); symbol.InvalidateVisual(); });
        ToolTip.SetTip(button, tip);
    }

    private Button Rail(string icon, string tip, Action? action)
    {
        var button = CommandButton(icon, "", tip, action);
        button.Height = 42;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        return button;
    }

    private IBrush ColorBrush(string name) => Brush.Parse(currentTheme.Colors[name]);

    private void ApplyTheme(ThemeDefinition theme)
    {
        ThemeService.Validate(theme);
        currentTheme = theme;
        Resources["TextBrush"] = ColorBrush("Muted");
        RequestedThemeVariant = theme.Id == "studiox.light" ? ThemeVariant.Light : ThemeVariant.Dark;
        Background = ColorBrush("Background");
        Foreground = ColorBrush("Text");
        editor.Background = ColorBrush("Background");
        editor.Foreground = ColorBrush("Text");
        editor.LineNumbersForeground = ColorBrush("Muted");
        log.Background = ColorBrush("Surface");
        log.Foreground = ColorBrush("Text");
        foreach (var paint in themePainters)
        {
            paint();
        }
        ApplySyntax();
        UpdateTabSelection();
    }

    private void InstallWorkbenchButtonTheme()
    {
        // 工具按钮不继承 Fluent 的系统禁用底色，避免主题切换残留浅色背景。
        var theme = new ControlTheme(typeof(Button));
        theme.Setters.Add(new Setter(Button.TemplateProperty, new FuncControlTemplate<Button>((button, _) =>
        {
            var presenter = new ContentPresenter();
            presenter.Bind(ContentPresenter.ContentProperty, new Binding(nameof(Button.Content)) { Source = button });
            presenter.Bind(ContentPresenter.HorizontalContentAlignmentProperty, new Binding(nameof(Button.HorizontalContentAlignment)) { Source = button });
            presenter.Bind(ContentPresenter.VerticalContentAlignmentProperty, new Binding(nameof(Button.VerticalContentAlignment)) { Source = button });
            var border = new Border { Child = presenter };
            border.Bind(Border.BackgroundProperty, new Binding(nameof(Button.Background)) { Source = button });
            border.Bind(Border.BorderBrushProperty, new Binding(nameof(Button.BorderBrush)) { Source = button });
            border.Bind(Border.BorderThicknessProperty, new Binding(nameof(Button.BorderThickness)) { Source = button });
            border.Bind(Border.PaddingProperty, new Binding(nameof(Button.Padding)) { Source = button });
            return border;
        })));
        theme.Children.Add(new Style(selector => selector.Nesting().Class(":disabled"))
        {
            Setters = { new Setter(OpacityProperty, 0.42) }
        });
        Styles.Add(new Style(selector => selector.OfType<Button>())
        {
            Setters = { new Setter(ThemeProperty, theme) }
        });
    }

    private static void AddAt(Grid parent, Control control, int row, int column = 0)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        parent.Children.Add(control);
    }

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void ToggleProject()
    {
        projectVisible = !projectVisible;
        body.ColumnDefinitions[1].Width = new GridLength(projectVisible ? WorkbenchVisuals.ProjectWidth : 0);
        body.ColumnDefinitions[2].Width = new GridLength(projectVisible ? 4 : 0);
    }

    private void ShowOutput(bool visible)
    {
        if (outputVisible && editorRegion.RowDefinitions[3].ActualHeight > 0)
        {
            outputHeight = editorRegion.RowDefinitions[3].ActualHeight;
        }
        outputVisible = visible;
        editorRegion.RowDefinitions[2].Height = new GridLength(visible ? 4 : 0);
        editorRegion.RowDefinitions[3].Height = new GridLength(visible ? outputHeight : 0);
    }
}
