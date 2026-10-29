namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using StudioX.Extensions.Abstractions;

/// <summary>只提供插件已声明的功能选项；菜单失效时拒绝迟到选择。</summary>
internal sealed class PluginMcuPinFunctionMenu : ContextMenu
{
    private bool dismissed;
    private readonly List<MenuItem> options = [];
    internal TextBox SearchBox { get; } = new() { MinWidth = 205, Margin = new Thickness(0, 4, 0, 4) };

    internal PluginMcuPinFunctionMenu(FrameworkElement anchor, PluginMcuPin pin, Func<string, Task> choose)
    {
        SetResourceReference(StyleProperty, "CodeContextMenu");
        PlacementTarget = anchor;
        Placement = PlacementMode.Right;
        MinWidth = 160;
        MaxHeight = 380;
        Items.Add(new MenuItem { Header = new TextBlock { Text = "PIN " + pin.Position + " · " + pin.Name, FontWeight = FontWeights.SemiBold }, IsEnabled = false });
        if (pin.Functions.Length == 0 || pin.Locked)
        {
            Items.Add(new MenuItem { Header = pin.Locked ? "已锁定，请右键解除锁定" : "当前预览只读", IsEnabled = false });
            return;
        }
        var clear = new MenuItem { Header = new TextBlock { Text = "Reset_State（未分配）" }, IsEnabled = pin.FunctionId.Length > 0 };
        clear.Click += async (_, _) => await Choose("");
        Items.Add(clear);
        Items.Add(new Separator());
        AutomationProperties.SetName(SearchBox, "搜索引脚功能");
        if (pin.Functions.Length > 12)
        {
            Items.Add(new MenuItem { Header = SearchBox, StaysOpenOnClick = true, Focusable = false });
        }
        foreach (var function in pin.Functions)
        {
            var item = new MenuItem { Header = new TextBlock { Text = function.Label }, Tag = function.Value, IsCheckable = true, IsChecked = pin.FunctionId == function.Value };
            item.Click += async (_, _) => await Choose(function.Value);
            options.Add(item);
            Items.Add(item);
        }
        var empty = new MenuItem { Header = "没有匹配的功能", IsEnabled = false, Visibility = Visibility.Collapsed };
        Items.Add(empty);
        SearchBox.TextChanged += (_, _) =>
        {
            foreach (var item in options)
            {
                item.Visibility = ((TextBlock)item.Header).Text.Contains(SearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
            }
            empty.Visibility = options.Any(item => item.Visibility == Visibility.Visible) ? Visibility.Collapsed : Visibility.Visible;
        };
        SearchBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Down or Key.Enter)
            {
                options.FirstOrDefault(item => item.Visibility == Visibility.Visible)?.Focus();
                e.Handled = true;
            }
        };
        Opened += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsOpen && pin.Functions.Length > 12)
            {
                SearchBox.Focus();
            }
        }));

        async Task Choose(string function)
        {
            if (dismissed)
            {
                return;
            }
            Dismiss();
            await choose(function);
        }
    }

    internal void Dismiss()
    {
        dismissed = true;
        IsOpen = false;
    }
}
