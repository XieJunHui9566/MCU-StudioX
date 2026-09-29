namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using StudioX.Engine;

/// <summary>单个封装脚的功能菜单；只报告用户选择，不修改工程或访问设备。</summary>
internal sealed class Ag32PinFunctionMenu : ContextMenu
{
    private readonly List<MenuItem> functionItems = [];
    private readonly MenuItem emptyItem = new() { Header = "没有匹配的功能", IsEnabled = false };
    private bool dismissed;

    internal TextBox SearchBox { get; } = new() { Width = 250, ToolTip = "搜索 GPIO、UART、SPI 等功能" };
    internal MenuItem ResetItem { get; } = new() { Header = new TextBlock { Text = "清除分配（Reset_State）" } };

    internal Ag32PinFunctionMenu(FrameworkElement anchor, Ag32PackagePin pin, Ag32PinFunction[] functions,
        IReadOnlyList<Ag32PinAssignment> assignments, bool editable, Action<Ag32PinFunction?> choose)
    {
        SetResourceReference(StyleProperty, "CodeContextMenu");
        PlacementTarget = anchor;
        Placement = PlacementMode.Right;
        MinWidth = 285;
        MaxWidth = 370;
        MaxHeight = 460;
        Items.Add(new MenuItem { Header = new TextBlock { Text = $"PIN {pin.Number}", FontWeight = FontWeights.SemiBold }, IsEnabled = false });
        if (!pin.CanAssign || !editable)
        {
            Items.Add(new MenuItem { Header = pin.CanAssign ? "当前 VE 为只读，请在文本编辑器修改" : "保留引脚 · 不可分配", IsEnabled = false });
            return;
        }

        ResetItem.IsEnabled = assignments.Any(assignment => assignment.PinNumber == pin.Number);
        ResetItem.Click += (_, _) => Select(null, choose);
        Items.Add(ResetItem);
        Items.Add(new Separator());
        Items.Add(new MenuItem { Header = SearchBox, StaysOpenOnClick = true, Focusable = false, Padding = new Thickness(8, 4, 8, 8) });
        foreach (var function in functions.OrderBy(function => function.Name, StringComparer.Ordinal))
        {
            var occupied = assignments.Where(assignment => assignment.Function == function.Name).ToArray();
            var current = occupied.Any(assignment => assignment.PinNumber == pin.Number);
            var elsewhere = occupied.Where(assignment => assignment.PinNumber != pin.Number).Select(assignment => $"PIN_{assignment.PinNumber}").ToArray();
            // 使用 TextBlock 保留 GPIO 名称中的下划线，避免被菜单访问键语法吞掉。
            var header = new StackPanel();
            header.Children.Add(new TextBlock { Text = Ag32PinFunctionLabels.Display(function.Name), FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal });
            if (current || elsewhere.Length != 0)
            {
                var note = new TextBlock
                {
                    Text = (current ? "当前分配" : "") + (elsewhere.Length == 0 ? "" : (current ? " · " : "") + string.Join("、", elsewhere) + " 已使用"),
                    FontSize = 11
                };
                note.SetResourceReference(TextBlock.ForegroundProperty, elsewhere.Length == 0 ? "RunColor" : "ErrorColor");
                header.Children.Add(note);
            }
            var item = new MenuItem
            {
                Header = header,
                Tag = function,
                Padding = new Thickness(10, 4, 10, 4),
                ToolTip = $"{function.Name} · {function.Direction}\n内部资源：{function.SharedGpio ?? "独立功能"}"
                    + (Ag32PinFunctionLabels.Description(function.Name) is { } description ? "\n" + description : "")
                    + (elsewhere.Length == 0 ? "" : "\n再次分配后会显示冲突，需要移除重复分配。")
            };
            item.Click += (_, _) => Select(function, choose);
            functionItems.Add(item);
            Items.Add(item);
        }
        Items.Add(emptyItem);
        emptyItem.Visibility = Visibility.Collapsed;
        SearchBox.TextChanged += (_, _) => Filter();
        SearchBox.PreviewKeyDown += Search_KeyDown;
        Opened += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsOpen)
            {
                SearchBox.Focus();
            }
        }));
    }

    internal void Dismiss()
    {
        dismissed = true;
        IsOpen = false;
    }

    private void Select(Ag32PinFunction? function, Action<Ag32PinFunction?> choose)
    {
        // WPF 先关闭菜单，再派发 Click；只拒绝被页面主动撤销的菜单，不能用 IsOpen 判断选择是否有效。
        if (dismissed)
        {
            return;
        }
        Dismiss();
        choose(function);
    }

    private void Filter()
    {
        var query = SearchBox.Text.Trim();
        foreach (var item in functionItems)
        {
            item.Visibility = Ag32PinFunctionLabels.Display(((Ag32PinFunction)item.Tag).Name).Contains(query, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
        }
        emptyItem.Visibility = functionItems.Any(item => item.Visibility == Visibility.Visible) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Enter)
        {
            functionItems.FirstOrDefault(item => item.Visibility == Visibility.Visible)?.Focus();
            e.Handled = true;
        }
    }
}
