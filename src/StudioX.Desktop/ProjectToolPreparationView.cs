namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using StudioX.Application.Distribution;
using StudioX.Application.Tools;

public sealed class ProjectToolPreparationView : UserControl
{
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
    private readonly TextBox source = new() { MinWidth = 200, Margin = new(0, 0, 0, 8) };
    private readonly TextBlock publisher = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) };
    private readonly DataGrid requirements = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, MinHeight = 90, MinRowHeight = 30 };
    private readonly TextBox detail = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new(10), MinHeight = 70 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, MaxHeight = 48, Margin = new(0, 0, 0, 8) };
    private readonly Dictionary<string, Button> buttons = [];
    private readonly RadioButton githubMode = new() { Content = "自动从 GitHub 获取", GroupName = "ComponentAcquisition", IsChecked = true, Margin = new(0, 0, 24, 8) };
    private readonly RadioButton manualMode = new() { Content = "手动导入本地组件", GroupName = "ComponentAcquisition", Margin = new(0, 0, 0, 8) };
    private readonly TextBlock modeHint = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) };
    private bool busy;
    public ProjectToolPlan? Plan
    {
        get; private set;
    }
    public DistributionListing? Listing
    {
        get; private set;
    }
    public ProjectToolRequirement? Selected => requirements.SelectedItem as ProjectToolRequirement;
    public string Source
    {
        get => source.Text; set => source.Text = value;
    }
    public Func<string, Task>? Requested
    {
        get; init;
    }
    public Action<ProjectToolRequirement>? SelectionChanged
    {
        get; init;
    }
    internal string DetailText => detail.Text;
    internal bool HasUsableSpace => requirements.ActualHeight >= 90 && detail.ActualHeight >= 70;
    internal bool CanDownload => buttons["download"].IsEnabled;
    internal bool CanAcquire => buttons["github-acquire"].IsEnabled;
    internal bool CanImport => buttons["offline"].IsEnabled;
    internal bool HasAcquisitionModes => githubMode.Content is not null && manualMode.Content is not null;
    internal void SelectManualMode(bool manual)
    {
        if (manual)
        {
            manualMode.IsChecked = true;
        }
        else
        {
            githubMode.IsChecked = true;
        }
    }
    internal bool HasTrustedCatalogAction => buttons.ContainsKey("trusted") && buttons.ContainsKey("library");
    internal bool PrimaryActionsVisible => buttons.Where(p => p.Key is "github-acquire" or "offline" or "repair" or "cancel")
        .Where(p => p.Value.Visibility == Visibility.Visible).All(p =>
        p.Value.ActualHeight > 0 && p.Value.TransformToAncestor(this).Transform(new Point()).Y + p.Value.ActualHeight <= ActualHeight);

    public ProjectToolPreparationView()
    {
        githubMode.SetResourceReference(ForegroundProperty, "Text");
        manualMode.SetResourceReference(ForegroundProperty, "Text");
        requirements.SetResourceReference(StyleProperty, "DebugGrid");
        var body = new Grid { Margin = new(16) };
        body.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        body.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        body.RowDefinitions.Add(new()
        {
            Height = new(1, GridUnitType.Star)
        });
        body.RowDefinitions.Add(new()
        {
            Height = new(1, GridUnitType.Star)
        });
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "准备工程开发环境组件", FontSize = 22 });
        top.Children.Add(summary);
        var actions = new WrapPanel();
        var primary = new WrapPanel();
        var advancedActions = new WrapPanel();
        foreach (var (label, action) in new[] { ("刷新检查", "refresh"), ("选择工程…", "project"), ("创建工程 / 导入器件包", "create"),
            ("已验证组件库", "trusted"), ("读取目录", "load"), ("选择离线目录…", "browse"), ("自定义目录公钥…", "key"), ("取消自定义公钥", "clear-key"), ("浏览 GitHub 组件库 / 其它版本", "library"),
            ("下载 / 继续并预览", "download"), ("获取并安装工程缺少的组件", "github-acquire"), ("选择 .mcutoolchain 并导入…", "offline"),
            ("组件校验与修复", "repair"), ("取消操作", "cancel"), ("开发环境组件管理", "management"), ("帮助", "help") })
        {
            var button = new Button { Content = label, Margin = new(0, 0, 8, 8), Padding = new(10, 5, 10, 5) };
            button.Click += async (_, _) => { if (Requested is { } run) { await run(action); } };
            var destination = action is "github-acquire" or "offline" or "repair" or "cancel" ? primary
                : action is "trusted" or "load" or "browse" or "key" or "clear-key" or "download" ? advancedActions : actions;
            destination.Children.Add(button);
            buttons.Add(action, button);
        }
        top.Children.Add(actions);
        var advanced = new StackPanel();
        advanced.Children.Add(new TextBlock { Text = "自定义目录仅用于明确选择的高级获取操作。自动模式始终使用 IDE 内置的 GitHub 来源和公钥。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) });
        advanced.Children.Add(source);
        advanced.Children.Add(publisher);
        advanced.Children.Add(advancedActions);
        var advancedPanel = new Expander { Header = "高级：自定义来源与离线目录", Content = advanced, Margin = new(0, 0, 0, 8) };
        advancedPanel.SetResourceReference(ForegroundProperty, "Text");
        top.Children.Add(advancedPanel);
        var modePanel = new StackPanel();
        var modes = new WrapPanel();
        modes.Children.Add(githubMode);
        modes.Children.Add(manualMode);
        modePanel.Children.Add(modes);
        modePanel.Children.Add(modeHint);
        modePanel.Children.Add(status);
        modePanel.Children.Add(primary);
        var scroll = new ScrollViewer { Content = top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        SizeChanged += (_, _) => scroll.MaxHeight = Math.Max(70, Math.Min((ActualHeight - 32) * .42, ActualHeight - 32 - modePanel.ActualHeight - 180));
        body.Children.Add(scroll);
        Grid.SetRow(modePanel, 1);
        body.Children.Add(modePanel);
        foreach (var (label, property, width) in new[] { ("用途", "Purpose", 130d), ("开发环境组件", "Id", 190d), ("锁定版本", "Version", 100d),
            ("状态", "StatusText", 120d), ("下载", "DownloadText", 150d), ("展开", "InstalledText", 110d) })
        {
            requirements.Columns.Add(new DataGridTextColumn { Header = label, Binding = new Binding(property), Width = width });
        }
        Grid.SetRow(requirements, 2);
        body.Children.Add(requirements);
        Grid.SetRow(detail, 3);
        body.Children.Add(detail);
        Content = body;
        requirements.SelectionChanged += (_, _) => { UpdateActions(); if (Selected is { } item) { SelectionChanged?.Invoke(item); } };
        source.TextChanged += (_, _) => SetListing(null);
        githubMode.Checked += (_, _) => UpdateActions();
        manualMode.Checked += (_, _) => UpdateActions();
        Source = TrustedDevelopmentCatalog.Source;
        UpdateActions();
    }
    public void SetPlan(ProjectToolPlan plan)
    {
        var previous = Selected;
        Plan = plan;
        summary.Text = plan.ProjectName + (plan.ProjectDirectory is null ? "" : " · " + plan.ProjectDirectory) + "\n" + plan.Message;
        requirements.ItemsSource = plan.Requirements;
        requirements.SelectedItem = plan.Requirements.FirstOrDefault(r => r.Id == previous?.Id && r.Version == previous.Version)
            ?? plan.Requirements.FirstOrDefault(r => r.State != ProjectToolState.Installed) ?? plan.Requirements.FirstOrDefault();
        if (Selected is null)
        {
            detail.Text = plan.Message;
        }
        UpdateActions();
    }
    public void SetListing(DistributionListing? listing)
    {
        Listing = listing;
        publisher.Text = listing is null ? "尚未读取目录。也可直接导入离线工具包。"
            : "发布者：" + listing.Catalog.Publisher + " · " + listing.Verification;
        UpdateActions();
    }
    public void SetDetail(string text) => detail.Text = text;
    public void InvalidatePlan(string message = "正在读取工程配置与必要工具入口…")
    {
        Plan = null;
        requirements.ItemsSource = null;
        detail.Clear();
        summary.Text = message;
        UpdateActions();
    }
    public void SetStatus(string text)
    {
        status.Text = text;
        status.ToolTip = text;
    }
    public void SetBusy(bool value)
    {
        busy = value;
        source.IsEnabled = !value;
        githubMode.IsEnabled = manualMode.IsEnabled = !value;
        UpdateActions();
    }
    private void UpdateActions()
    {
        foreach (var button in buttons.Values)
        {
            button.IsEnabled = !busy;
        }
        var automatic = githubMode.IsChecked == true;
        modeHint.Text = automatic ? "按工程锁定版本自动匹配、下载、校验并安装。无需填写地址；下载可取消并在下次继续。"
            : "选择相同 ID、版本与编译器的本地归档，可一次导入多个缺失组件。此模式不访问网络。";
        buttons["github-acquire"].Visibility = automatic ? Visibility.Visible : Visibility.Collapsed;
        buttons["offline"].Visibility = automatic ? Visibility.Collapsed : Visibility.Visible;
        var missing = Plan?.ProjectDirectory is not null && Plan.Requirements.Any(r => r.State == ProjectToolState.Missing);
        buttons["github-acquire"].IsEnabled = !busy && missing;
        buttons["download"].IsEnabled = !busy && Listing is not null && Selected is { State: ProjectToolState.Missing, Entry: not null };
        buttons["offline"].IsEnabled = !busy && missing;
        buttons["repair"].IsEnabled = !busy && Selected is not null;
        buttons["cancel"].IsEnabled = busy;
        buttons["cancel"].Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
}
