namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using StudioX.Application.Peripherals;
using StudioX.Engine.Svd;

public sealed class PeripheralView : UserControl
{
    private readonly TextBox search = new() { Margin = new(0, 8, 0, 8), ToolTip = "按外设、寄存器、地址或说明搜索" };
    private readonly ListBox list = new() { DisplayMemberPath = "Path", MinWidth = 220 };
    private readonly TextBox detail = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly DataGrid fields = new() { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, MinHeight = 100 };
    private readonly TextBox writeValue = new() { Width = 170, ToolTip = "完整寄存器值：十进制或 0x 十六进制" };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    public Func<string, Task>? Requested { get; set; }
    public PeripheralDocument? Document { get; private set; }
    public SvdRegister? Selected => list.SelectedItem as SvdRegister;
    internal string DetailText => detail.Text;
    internal string StatusText => status.Text;
    public ulong WriteValue => writeValue.Text.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? ulong.Parse(writeValue.Text.Trim()[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : ulong.Parse(writeValue.Text.Trim(), CultureInfo.InvariantCulture);
    public PeripheralView()
    {
        fields.SetResourceReference(StyleProperty, "DebugGrid");
        foreach (var (header, width) in new[] { ("位域", 90), ("位", 85), ("访问", 120), ("当前值", 90), ("枚举", 140) })
        {
            fields.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(header), Width = width, MinWidth = width });
        }
        fields.Columns.Add(new DataGridTextColumn { Header = "说明", Binding = new Binding("说明"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 100 });
        list.SetResourceReference(Control.BackgroundProperty, "Panel");
        list.SetResourceReference(Control.ForegroundProperty, "Text");
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Text")));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
        list.ItemContainerStyle = itemStyle;
        search.ToolTip = "搜索：外设、寄存器、地址或说明";
        var body = new DockPanel { Margin = new(16) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "外设寄存器 · CMSIS-SVD", FontSize = 22 });
        top.Children.Add(new TextBlock { Text = "先核对 SVD 与工程器件。只在已校验固件的实机暂停会话中读写；不自动轮询外设。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) });
        var actions = new WrapPanel();
        foreach (var (title, action) in new[] { ("导入并绑定 SVD…", "import"), ("读取选中寄存器", "read"), ("确认完整值写入…", "write") })
        {
            var button = new Button { Content = title, Margin = new(0, 0, 8, 8), Padding = new(10, 5, 10, 5) };
            button.Click += async (_, _) => { if (Requested is { } run) { await run(action); } };
            actions.Children.Add(button);
        }
        actions.Children.Add(new TextBlock { Text = "写入值", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 8) });
        actions.Children.Add(writeValue); top.Children.Add(actions); top.Children.Add(status);
        top.Children.Add(new TextBlock { Text = "搜索寄存器", Margin = new(0, 8, 0, 0) }); top.Children.Add(search);
        DockPanel.SetDock(top, Dock.Top); body.Children.Add(top);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = new(2, GridUnitType.Star) });
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        grid.Children.Add(list);
        var right = new Grid { Margin = new(12, 0, 0, 0) }; right.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); right.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        right.Children.Add(detail); Grid.SetRow(fields, 1); right.Children.Add(fields); Grid.SetColumn(right, 1); grid.Children.Add(right);
        body.Children.Add(grid); Content = body;
        search.TextChanged += (_, _) => Filter();
        list.SelectionChanged += (_, _) => ClearReading("选择已变化，请显式读取。");
    }
    public void SetDocument(PeripheralDocument? document)
    {
        Document = document; Filter(); ClearReading(document is null ? "当前工程尚未绑定 SVD。" : $"{document.Binding.DeviceId} · SVD {document.Device.Name} · {document.Device.Registers.Count} 个寄存器 · SHA-256 {document.Device.Sha256}");
    }
    private void Filter()
    {
        var selected = Selected;
        var query = search.Text.Trim();
        list.ItemsSource = Document?.Device.Registers.Where(r => (r.Path + " " + r.Description + $" 0x{r.Address:x8}").Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        list.SelectedItem = selected is not null && list.Items.Contains(selected) ? selected : null;
    }
    public void ClearReading(string reason)
    {
        status.Text = reason;
        if (Selected is not { } register) { detail.Text = "选择一个寄存器查看位域与访问限制。"; fields.ItemsSource = null; return; }
        detail.Text = $"{register.Path}\n地址 0x{register.Address:x8} · {register.Width} 位 · {register.Access}\n复位值 {(register.ResetValue is { } value ? $"0x{value:x}" : "未声明")}（不是当前值）\n{register.Description}\n读取副作用：{(register.HasReadSideEffects ? "有；需要单独确认" : "SVD 未声明")}\n写入语义：{register.WriteSemantics}\n当前值：尚未读取";
        fields.ItemsSource = register.Fields.Select(f => new { 位域 = f.Name, 位 = $"[{f.Offset + f.Width - 1}:{f.Offset}]", 访问 = f.Access, 当前值 = "未读取", 枚举 = string.Join("; ", f.Enumerations.Select(e => $"{e.Key}={e.Value}")), 说明 = f.Description }).ToArray();
    }
    public void SetReading(PeripheralReading reading)
    {
        if (Selected is not { } r || r.Path != reading.Register) { return; }
        ClearReading($"宿主请求时间 {reading.RequestedAtUtc:O} · 暂停目标读数");
        detail.Text = detail.Text.Replace("当前值：尚未读取", $"当前值：0x{reading.Value:x} · {reading.Value}", StringComparison.Ordinal);
        fields.ItemsSource = r.Fields.Select(f => new { 位域 = f.Name, 位 = $"[{f.Offset + f.Width - 1}:{f.Offset}]", 访问 = f.Access, 当前值 = f.Access is "read-only" or "read-write" or "read-writeOnce" ? f.Extract(reading.Value).ToString(CultureInfo.InvariantCulture) : "不可读", 枚举 = f.Enumerations.GetValueOrDefault(f.Extract(reading.Value), ""), 说明 = f.Description }).ToArray();
    }
    internal void SelectRegister(string path) => list.SelectedItem = Document?.Device.Registers.Single(r => r.Path == path);
}
