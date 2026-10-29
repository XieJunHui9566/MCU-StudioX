namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using StudioX.Extensions.Abstractions;

internal sealed partial class PluginMcuConfigurationView
{
    private readonly DispatcherTimer clockUpdateTimer = new(DispatcherPriority.Background);
    private readonly Dictionary<string, FrameworkElement> fieldControls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> acceptedFieldText = new(StringComparer.Ordinal);
    private FieldFocus? submittedFieldFocus;
    private bool restoringFieldFocus;
    private long stateRevision;

    private sealed record FieldFocus(string Id, string Value, int Start, int Length);

    private void TrackClockField(PluginMcuField field, FrameworkElement control)
    {
        fieldControls[field.Id] = control;
        if (control is ComboBox select)
        {
            select.SelectionChanged += (_, _) => QueueClockUpdate(select.IsEditable);
            if (select.IsEditable)
            {
                select.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => QueueClockUpdate(typing: true)));
            }
        }
        else if (control is TextBox input)
        {
            input.TextChanged += (_, _) => QueueClockUpdate(typing: true);
        }
    }

    private void QueueClockUpdate(bool typing)
    {
        if (restoringFieldFocus || !HasUnappliedFields())
        {
            return;
        }
        // 合并下拉与文本事件；暂缓不完整输入，不在宿主推断任何 MCU 时钟规则。
        clockUpdateTimer.Stop();
        clockUpdateTimer.Interval = TimeSpan.FromMilliseconds(typing ? 400 : 80);
        clockUpdateTimer.Start();
    }

    private async Task ApplyClockFieldsAsync()
    {
        clockUpdateTimer.Stop();
        if (!IsLoaded || !HasUnappliedFields())
        {
            return;
        }
        if (busy || !IsEnabled)
        {
            clockUpdateTimer.Start();
            return;
        }
        await Send(new
        {
            action = "parameters",
            values = fields.ToDictionary(item => item.Key, item => item.Value(), StringComparer.Ordinal)
        });
    }

    private FieldFocus? CaptureFieldFocus()
    {
        var focused = fieldControls.FirstOrDefault(item => item.Value.IsKeyboardFocusWithin);
        if (focused.Key is null || !fields.TryGetValue(focused.Key, out var value))
        {
            return null;
        }
        var text = Keyboard.FocusedElement as TextBox;
        return new(focused.Key, value(), text?.SelectionStart ?? 0, text?.SelectionLength ?? 0);
    }

    private void RestoreFieldFocus(FieldFocus? focus, bool preserveText)
    {
        if (focus is null || !fieldControls.TryGetValue(focus.Id, out var control))
        {
            return;
        }
        restoringFieldFocus = true;
        try
        {
            TextBox? text = control as TextBox;
            if (control is ComboBox { IsEditable: true } select)
            {
                if (preserveText)
                {
                    select.Text = focus.Value;
                    acceptedFieldText[focus.Id] = focus.Value;
                }
                select.ApplyTemplate();
                text = select.Template.FindName("PART_EditableTextBox", select) as TextBox;
            }
            else if (text is not null && preserveText)
            {
                text.Text = focus.Value;
                acceptedFieldText[focus.Id] = focus.Value;
            }
            if (control.IsEnabled)
            {
                if (text is not null)
                {
                    text.Focus();
                    text.Select(Math.Min(focus.Start, text.Text.Length), Math.Min(focus.Length, Math.Max(0, text.Text.Length - focus.Start)));
                }
                else
                {
                    control.Focus();
                }
            }
        }
        finally
        {
            restoringFieldFocus = false;
        }
    }

    private void WarnClockUpdate()
    {
        if (pendingNotice is not null)
        {
            pendingNotice.Text = Page == 1 ? "时钟参数未能更新，图中仍为上次配置；请检查输入或查看插件诊断。"
                : "外设参数未能更新，时钟树仍为上次配置；请检查输入或查看插件诊断。";
            pendingNotice.Visibility = Visibility.Visible;
        }
    }
}
