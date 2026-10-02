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
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };
    private readonly Dictionary<string, Button> buttons = [];
    private bool busy;
    public ProjectToolPlan? Plan { get; private set; }
    public DistributionListing? Listing { get; private set; }
    public ProjectToolRequirement? Selected => requirements.SelectedItem as ProjectToolRequirement;
    public string Source { get => source.Text; set => source.Text = value; }
    public Func<string, Task>? Requested { get; init; }
    public Action<ProjectToolRequirement>? SelectionChanged { get; init; }
    internal string DetailText => detail.Text;
    internal bool HasUsableSpace => requirements.ActualHeight >= 90 && detail.ActualHeight >= 70;
    internal bool CanDownload => buttons["download"].IsEnabled;
    internal bool PrimaryActionsVisible => buttons.Where(p => p.Key is "download" or "offline" or "repair").All(p =>
        p.Value.ActualHeight > 0 && p.Value.TransformToAncestor(this).Transform(new Point()).Y + p.Value.ActualHeight <= ActualHeight);

    public ProjectToolPreparationView()
    {
        requirements.SetResourceReference(StyleProperty, "DebugGrid");
        var body = new Grid { Margin = new(16) };
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "准备工程工具", FontSize = 22 });
        top.Children.Add(summary);
        top.Children.Add(new TextBlock { Text = "工具来源：HTTPS 目录地址或本地 catalog.json。点击读取后才访问来源。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 8) });
        top.Children.Add(source);
        top.Children.Add(publisher);
        var actions = new WrapPanel();
        var primary = new WrapPanel();
        foreach (var (label, action) in new[] { ("刷新检查", "refresh"), ("选择工程…", "project"), ("创建工程 / 导入器件包", "create"),
            ("读取目录", "load"), ("选择离线目录…", "browse"), ("发布者公钥…", "key"), ("取消公钥验证", "clear-key"),
            ("下载 / 继续并预览", "download"), ("导入离线工具包…", "offline"), ("工具校验与修复", "repair"), ("占用与升级管理", "management"), ("帮助", "help") })
        {
            var button = new Button { Content = label, Margin = new(0, 0, 8, 8), Padding = new(10, 5, 10, 5) };
            button.Click += async (_, _) => { if (Requested is { } run) await run(action); };
            (action is "download" or "offline" or "repair" ? primary : actions).Children.Add(button); buttons.Add(action, button);
        }
        top.Children.Add(actions); top.Children.Add(status);
        var scroll = new ScrollViewer { Content = top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        SizeChanged += (_, _) => scroll.MaxHeight = Math.Max(70, Math.Min((ActualHeight - 32) * .42, ActualHeight - 32 - primary.ActualHeight - 180));
        body.Children.Add(scroll);
        Grid.SetRow(primary, 1); body.Children.Add(primary);
        foreach (var (label, property, width) in new[] { ("用途", "Purpose", 130d), ("工具集", "Id", 190d), ("锁定版本", "Version", 100d),
            ("状态", "StatusText", 120d), ("下载", "DownloadText", 150d), ("展开", "InstalledText", 110d) })
            requirements.Columns.Add(new DataGridTextColumn { Header = label, Binding = new Binding(property), Width = width });
        Grid.SetRow(requirements, 2); body.Children.Add(requirements);
        Grid.SetRow(detail, 3); body.Children.Add(detail); Content = body;
        requirements.SelectionChanged += (_, _) => { UpdateActions(); if (Selected is { } item) SelectionChanged?.Invoke(item); };
        source.TextChanged += (_, _) => SetListing(null);
        UpdateActions();
    }
    public void SetPlan(ProjectToolPlan plan)
    {
        var previous = Selected;
        Plan = plan; summary.Text = plan.ProjectName + (plan.ProjectDirectory is null ? "" : " · " + plan.ProjectDirectory) + "\n" + plan.Message;
        requirements.ItemsSource = plan.Requirements;
        requirements.SelectedItem = plan.Requirements.FirstOrDefault(r => r.Id == previous?.Id && r.Version == previous.Version)
            ?? plan.Requirements.FirstOrDefault(r => r.State != ProjectToolState.Installed) ?? plan.Requirements.FirstOrDefault();
        if (Selected is null) detail.Text = plan.Message;
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
        Plan = null; requirements.ItemsSource = null; detail.Clear(); summary.Text = message; UpdateActions();
    }
    public void SetStatus(string text) => status.Text = text;
    public void SetBusy(bool value) { busy = value; source.IsEnabled = !value; UpdateActions(); }
    private void UpdateActions()
    {
        foreach (var button in buttons.Values) button.IsEnabled = !busy;
        buttons["download"].IsEnabled = !busy && Listing is not null && Selected is { State: ProjectToolState.Missing, Entry: not null };
        buttons["offline"].IsEnabled = !busy && Selected is { State: ProjectToolState.Missing };
        buttons["repair"].IsEnabled = !busy && Selected is not null;
    }
}
