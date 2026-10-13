namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StudioX.Application.BuildConfiguration;
using StudioX.Application.Editing;

/// <summary>明确选择构建文件、目标和源码操作；应用只返回经预览的缓冲区计划。</summary>
internal sealed class SourceRegistrationWindow : Window
{
    internal ComboBox Configuration { get; } = new() { MinWidth = 300, Margin = new(0, 0, 10, 0) };
    internal ComboBox Target { get; } = new() { MinWidth = 180 };
    internal ListBox Operations { get; } = new() { SelectionMode = SelectionMode.Extended, DisplayMemberPath = nameof(SourceRegistrationOperation.Description) };
    internal TextBox Before { get; } = PreviewBox();
    internal TextBox After { get; } = PreviewBox();
    internal Button Preview { get; } = new() { Content = "预览修改", Padding = new(14, 7, 14, 7), Margin = new(0, 0, 10, 0) };
    internal Button Apply { get; } = new() { Content = "应用到编辑器", Padding = new(14, 7, 14, 7), IsEnabled = false };
    internal TextBlock Status { get; } = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 12) };
    private readonly SourceRegistrationService service;
    private readonly IReadOnlyList<string> sources;
    private readonly IReadOnlyList<ProjectFileChange> moves;
    private readonly Func<IReadOnlyList<WorkspaceBufferSnapshot>> buffers;
    private readonly Action<string> log;
    private readonly string? selected;
    private SourceRegistrationContext context;
    private bool busy;
    private long reading;
    internal SourceRegistrationPlan? Plan
    {
        get; private set;
    }
    public SourceRegistrationWindow(SourceRegistrationService service, SourceRegistrationContext context, IReadOnlyList<string> configurations,
        IReadOnlyList<string> sources, IReadOnlyList<ProjectFileChange> moves, Func<IReadOnlyList<WorkspaceBufferSnapshot>> buffers, string? selected, Action<string> log)
    {
        this.service = service;
        this.context = context;
        this.sources = sources;
        this.moves = moves;
        this.buffers = buffers;
        this.selected = selected;
        this.log = log;
        Title = "源码登记与编译列表";
        Width = 1120;
        Height = 750;
        MinWidth = 760;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Surface");
        SetResourceReference(ForegroundProperty, "Text");
        Operations.SetResourceReference(BackgroundProperty, "ToolSurface");
        Operations.SetResourceReference(ForegroundProperty, "Text");
        Operations.SetResourceReference(BorderBrushProperty, "Border");
        Operations.ItemContainerStyle = new Style(typeof(ListBoxItem), (Style)FindResource("CodeCompletionItemStyle"));
        var root = new Grid { Margin = new(18) };
        root.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        root.RowDefinitions.Add(new());
        root.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        var heading = new StackPanel { Margin = new(0, 0, 0, 14) };
        heading.Children.Add(new TextBlock { Text = "核对新增、移除及改名的源码 · Ctrl / Shift 多选 · 移除登记会保留磁盘文件", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
        var selectors = new WrapPanel();
        selectors.Children.Add(new TextBlock { Text = "构建文件  ", VerticalAlignment = VerticalAlignment.Center });
        selectors.Children.Add(Configuration);
        selectors.Children.Add(new TextBlock { Text = "目标  ", VerticalAlignment = VerticalAlignment.Center });
        selectors.Children.Add(Target);
        heading.Children.Add(selectors);
        root.Children.Add(heading);
        var body = new Grid();
        body.ColumnDefinitions.Add(new()
        {
            Width = new GridLength(340)
        });
        body.ColumnDefinitions.Add(new());
        Operations.Margin = new(0, 0, 12, 0);
        body.Children.Add(Operations);
        var comparison = new Grid();
        comparison.ColumnDefinitions.Add(new());
        comparison.ColumnDefinitions.Add(new());
        comparison.Children.Add(Pane("修改前（含未保存内容）", Before));
        var afterPane = Pane("修改后", After);
        Grid.SetColumn(afterPane, 1);
        comparison.Children.Add(afterPane);
        Grid.SetColumn(comparison, 1);
        body.Children.Add(comparison);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        var footer = new StackPanel();
        footer.Children.Add(Status);
        footer.Children.Add(new TextBlock { Text = "修改进入未保存缓冲区，可一次撤销。保存后重新配置或编译，才能刷新实际编译数据库；本窗口不运行构建。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
        var buttons = new WrapPanel();
        buttons.Children.Add(Preview);
        buttons.Children.Add(Apply);
        var close = new Button { Content = "关闭", IsCancel = true, Padding = new(14, 7, 14, 7), Margin = new(10, 0, 0, 0) };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
        System.Windows.Automation.AutomationProperties.SetName(Operations, "源码登记候选与移除操作");
        Configuration.ItemsSource = configurations;
        Configuration.SelectedItem = context.Configuration.Source.RelativePath;
        Configuration.SelectionChanged += async (_, _) => await ReadConfigurationAsync();
        Target.SelectionChanged += (_, _) => { if (!busy) { RefreshOperations(); } };
        Operations.SelectionChanged += (_, _) => Invalidate();
        Preview.Click += (_, _) => PreviewSelected();
        Apply.Click += (_, _) => { if (Plan is not null) { DialogResult = true; } };
        Closed += (_, _) => reading++;
        SetContext(context);
    }
    private void SetContext(SourceRegistrationContext value)
    {
        busy = true;
        context = value;
        Target.ItemsSource = value.Targets.Select(target => target.Name).ToArray();
        Target.SelectedIndex = value.Targets.Count == 1 ? 0 : -1;
        busy = false;
        Before.Text = Display(value.Configuration.Text);
        RefreshOperations();
    }
    private async Task ReadConfigurationAsync()
    {
        var version = ++reading;
        Invalidate();
        Operations.ItemsSource = null;
        Target.ItemsSource = null;
        if (Configuration.SelectedItem is not string path)
        {
            return;
        }
        Status.Text = "正在读取构建文件…";
        busy = true;
        Preview.IsEnabled = false;
        try
        {
            var value = await service.ReadAsync(context.ProjectDirectory, path, buffers());
            if (version != reading)
            {
                return;
            }
            SetContext(value);
        }
        catch (Exception error) { if (version == reading) { Before.Clear(); Status.Text = error.Message; log(error.ToString()); } }
        finally { if (version == reading) { busy = false; Preview.IsEnabled = Target.SelectedItem is not null; } }
    }
    private void RefreshOperations()
    {
        Invalidate();
        Operations.ItemsSource = null;
        if (Target.SelectedItem is not string target)
        {
            Status.Text = "此文件包含多个目标，请明确选择。";
            Preview.IsEnabled = false;
            return;
        }
        try
        {
            var suggested = service.Suggest(context, target, sources, moves);
            var live = context.Targets.Single(item => item.Name == target).Sources.Where(SourceRegistrationService.IsSource)
                .Where(path => sources.Contains(path, StringComparer.OrdinalIgnoreCase) && !suggested.Any(operation => operation.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                .Select(path => new SourceRegistrationOperation(SourceRegistrationKind.Remove, path));
            var operations = suggested.Concat(live).ToArray();
            Operations.ItemsSource = operations;
            Preview.IsEnabled = true;
            foreach (var operation in operations.Where(operation => operation.Kind != SourceRegistrationKind.Remove &&
                (operation.Path.Equals(selected, StringComparison.OrdinalIgnoreCase) || operation.NewPath?.Equals(selected, StringComparison.OrdinalIgnoreCase) == true)))
            {
                Operations.SelectedItems.Add(operation);
            }
            Status.Text = $"{suggested.Count} 项待核对建议 · 当前目标登记 {context.Targets.Single(item => item.Name == target).Sources.Count} 项。请选择要应用的操作。";
        }
        catch (Exception error) { Status.Text = error.Message; Preview.IsEnabled = false; log(error.ToString()); }
    }
    internal void PreviewSelected()
    {
        Invalidate();
        if (busy || Target.SelectedItem is not string target)
        {
            return;
        }
        try
        {
            Plan = service.Prepare(context, target, Operations.SelectedItems.Cast<SourceRegistrationOperation>().ToArray());
            After.Text = Display(Plan.Change.After);
            Apply.IsEnabled = Plan.Change.CanApply;
            Status.Text = Apply.IsEnabled ? $"将修改 {Plan.Change.Path} · {Plan.Operations.Count} 项操作。请审阅后应用。" : "选中源码已经登记，无需修改。";
        }
        catch (Exception error) { Plan = null; Status.Text = error.Message; log(error.ToString()); }
    }
    private void Invalidate()
    {
        Plan = null;
        Apply.IsEnabled = false;
        After.Clear();
    }
    private static string Display(string value) => value.Length <= 200000 ? value : value[..200000] + "\n（展示已截短，计划保留完整文本。）";
    private static TextBox PreviewBox()
    {
        var box = new TextBox { IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        box.SetResourceReference(BackgroundProperty, "EditorSurface");
        box.SetResourceReference(ForegroundProperty, "Text");
        box.SetResourceReference(BorderBrushProperty, "Border");
        return box;
    }
    private static Grid Pane(string title, TextBox text)
    {
        var pane = new Grid { Margin = new(5, 0, 5, 0) };
        pane.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        pane.RowDefinitions.Add(new());
        pane.Children.Add(new TextBlock { Text = title, Margin = new(0, 0, 0, 6) });
        Grid.SetRow(text, 1);
        pane.Children.Add(text);
        return pane;
    }
}
