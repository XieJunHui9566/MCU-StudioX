namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using StudioX.Extensions.Abstractions;

/// <summary>引脚命名与锁定菜单；动作由提供者校验，旧菜单关闭后拒绝迟到操作。</summary>
internal sealed class PluginMcuPinContextMenu : ContextMenu
{
    private bool dismissed;
    internal TextBox LabelBox
    {
        get;
    }
    internal MenuItem ApplyLabelItem
    {
        get;
    }
    internal MenuItem LockItem
    {
        get;
    }

    internal PluginMcuPinContextMenu(FrameworkElement anchor, PluginMcuPin pin, Func<object, Task> submit)
    {
        SetResourceReference(StyleProperty, "CodeContextMenu");
        PlacementTarget = anchor;
        Placement = PlacementMode.Right;
        MinWidth = 170;
        Items.Add(new MenuItem { Header = pin.Name, IsEnabled = false });
        LabelBox = new TextBox { Text = pin.Label, MaxLength = 128, MinWidth = 180, Margin = new Thickness(6) };
        AutomationProperties.SetName(LabelBox, "引脚用户名称");
        ApplyLabelItem = new MenuItem { Header = "应用名称" };
        var name = new MenuItem { Header = "设置用户名称", IsEnabled = pin.CanEditMetadata };
        name.Items.Add(new MenuItem { Header = LabelBox, StaysOpenOnClick = true, Focusable = false });
        name.Items.Add(ApplyLabelItem);
        Items.Add(name);
        LockItem = new MenuItem
        {
            Header = pin.Locked ? "解除引脚锁定" : "锁定引脚",
            IsCheckable = true,
            IsChecked = pin.Locked,
            IsEnabled = pin.CanEditMetadata
        };
        Items.Add(LockItem);
        if (!pin.CanEditMetadata)
        {
            Items.Add(new MenuItem { Header = "请先分配可编辑功能", IsEnabled = false });
        }
        ApplyLabelItem.Click += async (_, _) => await RenameAsync();
        LabelBox.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await RenameAsync();
            }
        };
        LockItem.Click += async (_, _) => await ChooseAsync(new { action = "pinLock", pinId = pin.Id, locked = !pin.Locked });

        Task RenameAsync() => ChooseAsync(new
        {
            action = "pinLabel",
            pinId = pin.Id,
            label = LabelBox.Text
        });
        async Task ChooseAsync(object arguments)
        {
            if (dismissed || !pin.CanEditMetadata)
            {
                return;
            }
            Dismiss();
            await submit(arguments);
        }
    }

    internal void Dismiss()
    {
        dismissed = true;
        IsOpen = false;
    }
}
