namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Application.Distribution;

public sealed class DistributionCenterView : UserControl
{
    private readonly TextBox source = new() { MinWidth = 200, Margin = new(0, 0, 8, 8) };
    private readonly TextBox search = new() { Width = 210, Margin = new(0, 0, 8, 8) };
    private readonly ComboBox kind = new() { ItemsSource = new[] { "全部", "工具集", "插件", "组件" }, SelectedIndex = 0, Width = 100, Margin = new(0, 0, 8, 8) };
    private readonly TextBox target = new() { Text = "firmware", Width = 140, Margin = new(0, 0, 8, 8) };
    private readonly DataGrid entries = new() { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, MinHeight = 90, MinRowHeight = 30 };
    private readonly TextBox detail = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 60, Padding = new(8) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
    public DistributionListing? Listing
    {
        get; private set;
    }
    public DistributionEntry? Selected => entries.SelectedItem as DistributionEntry;
    public string Source
    {
        get => source.Text; set => source.Text = value;
    }
    public string ComponentTarget => target.Text.Trim();
    internal int VisibleEntryCount => entries.Items.Count;
    internal string DetailText => detail.Text;
    internal bool ListHasSpace => entries.ActualHeight >= 90 && detail.ActualHeight >= 60;
    internal void SetFilter(string text, int kindIndex)
    {
        search.Text = text;
        kind.SelectedIndex = kindIndex;
    }
    internal void SelectFirst() => entries.SelectedIndex = entries.Items.Count > 0 ? 0 : -1;
    public Func<string, Task>? Requested
    {
        get; set;
    }
    public Action<DistributionEntry>? SelectionChanged
    {
        get; set;
    }
    public DistributionCenterView()
    {
        entries.SetResourceReference(StyleProperty, "DebugGrid");
        var body = new Grid { Margin = new(16) };
        body.RowDefinitions.Add(new()
        {
            Height = GridLength.Auto
        });
        body.RowDefinitions.Add(new()
        {
            Height = new(2, GridUnitType.Star)
        });
        body.RowDefinitions.Add(new()
        {
            Height = new(1, GridUnitType.Star)
        });
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "软件与组件分发", FontSize = 22 });
        top.Children.Add(new TextBlock { Text = "从发布者目录选择精确版本；支持 HTTPS 和离线目录。下载与安装均由按钮触发，工具版本并存，插件安装后需到管理页确认启用。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) });
        top.Children.Add(source);
        var filters = new WrapPanel();
        filters.Children.Add(new TextBlock { Text = "搜索", Margin = new(0, 5, 8, 0) });
        filters.Children.Add(search);
        filters.Children.Add(kind);
        filters.Children.Add(new TextBlock { Text = "组件 CMake 目标", Margin = new(0, 5, 8, 0) });
        filters.Children.Add(target);
        top.Children.Add(filters);
        var buttons = new WrapPanel();
        foreach (var (title, action) in new[] { ("读取目录", "load"), ("选择离线目录", "browse"), ("选择发布者公钥", "key"), ("取消公钥验证", "clear-key"), ("工程所需工具", "required"), ("下载并预览安装", "install"), ("导入本地组件", "component"), ("回退插件", "plugin-rollback"), ("回退组件", "component-rollback"), ("查看工程组件", "components") })
        {
            var button = new Button { Content = title, Margin = new(0, 0, 8, 8), Padding = new(10, 5, 10, 5) };
            button.Click += async (_, _) => { if (Requested is { } run) { await run(action); } };
            buttons.Children.Add(button);
        }
        top.Children.Add(buttons);
        top.Children.Add(status);
        var controls = new ScrollViewer { Content = top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        // 小窗口限制工具栏高度，保留列表和详情空间；较多操作可在工具栏内滚动。
        SizeChanged += (_, _) => controls.MaxHeight = Math.Max(100, (ActualHeight - 32) * .45);
        body.Children.Add(controls);
        Grid.SetRow(detail, 2);
        body.Children.Add(detail);
        foreach (var (header, property, width) in new[] { ("类型", "Kind", 70d), ("名称", "Name", 180d), ("ID", "Id", 180d), ("版本", "Version", 85d), ("下载字节", "DownloadBytes", 110d), ("许可证", "License", 100d) })
        {
            entries.Columns.Add(new DataGridTextColumn { Header = header, Binding = new System.Windows.Data.Binding(property), Width = width });
        }
        Grid.SetRow(entries, 1);
        body.Children.Add(entries);
        Content = body;
        search.TextChanged += (_, _) => Filter();
        kind.SelectionChanged += (_, _) => Filter();
        entries.SelectionChanged += (_, _) => { if (Selected is { } entry) { SelectionChanged?.Invoke(entry); } };
    }
    public void SetListing(DistributionListing listing)
    {
        Listing = listing;
        Source = listing.Source;
        status.Text = $"发布者：{listing.Catalog.Publisher} · {listing.Verification}\n目录 SHA-256：{listing.CatalogSha256}";
        Filter();
    }
    private void Filter()
    {
        var wanted = kind.SelectedIndex switch
        {
            1 => "tool",
            2 => "plugin",
            3 => "component",
            _ => null
        };
        entries.ItemsSource = Listing?.Catalog.Entries.Where(e => (wanted is null || e.Kind == wanted) && (e.Id + " " + e.Name + " " + e.License).Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    public void ShowRequired(string id, string version)
    {
        kind.SelectedIndex = 1;
        search.Text = id;
        entries.SelectedItem = Listing?.Catalog.Entries.FirstOrDefault(e => e.Kind == "tool" && e.Id == id && e.Version == version);
        if (entries.SelectedItem is null)
        {
            status.Text = $"当前目录没有工程锁定的 {id}/{version}；请选择包含此版本的目录或离线工具归档。";
        }
    }
    public void SetDetail(string text) => detail.Text = text;
    public void SetStatus(string text) => status.Text = text;
}
