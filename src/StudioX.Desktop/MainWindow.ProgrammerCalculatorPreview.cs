namespace StudioX.Desktop;

using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using StudioX.Application;

public partial class MainWindow
{
    private async Task CheckProgrammerWindowAsync(string directory, Action<bool, string> check)
    {
        const string id = "studiox.bit-lab";
        var activity = pluginActivities[id];
        var tool = pluginWindows[id + "/lab"].Window;
        var view = (ProgrammerCalculatorView)((ContentControl)tool.Content).Content;
        async Task Layout()
        {
            tool.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        async Task Click(Button button, Func<bool> ready)
        {
            check(button.IsEnabled, "calculator action is available: " + AutomationProperties.GetName(button) + button.Content);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await view.InputIdle;
            await pluginInvocationTask;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            do
            {
                FlushPluginPanels();
                await Layout();
                if (ready())
                {
                    return;
                }
                await Task.Delay(20, timeout.Token);
            } while (true);
        }
        Button Key(string key) => view.Keypad.Children.OfType<Button>().Single(button => Equals(button.Content, key));
        check(!tool.IsVisible, "plugin activation prepares calculator without unsolicited popup");
        activity.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Layout();
        check(tool.IsVisible && !activity.Tab.IsSelected && tool.Owner == this && tool.ShowInTaskbar, "rail opens a modeless independent taskbar window");
        activity.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(ReferenceEquals(tool, pluginWindows[id + "/lab"].Window), "repeated open reuses the calculator window");
        check(!Key("A").IsEnabled && Key("9").IsEnabled, "DEC keypad disables hexadecimal digits");
        await CheckRealtimeCalculatorAsync(view, directory, check);
        var dec = view.BaseRows.Children.OfType<Button>().Single(button => AutomationProperties.GetName(button) == "DEC 输入进制");
        await Click(dec, () => !Key("A").IsEnabled);
        var dword = view.WidthButtons.Children.OfType<Button>().Single(button => Equals(button.Content, "DWORD"));
        await Click(dword, () => view.Bits.Children.Count == 32);
        view.Expression.Text = "0xFFFFFFFF";
        await Click(Key("="), () => view.Constant.Text.Contains("FFFFFFFF", StringComparison.Ordinal));
        check(view.Bits.Children.Count == 32 && view.LittleEndian.Text == "FF FF FF FF", "process result populates bit grid and bytes");
        view.Expression.Text = "1/0";
        await Click(Key("="), () => view.StatusText.Text.Contains("除数不能为 0", StringComparison.Ordinal));
        check(view.Expression.Text == "1/0" && view.Constant.Text.Contains("FFFFFFFF", StringComparison.Ordinal), "error preserves edited expression and previous successful result");
        view.Expression.Text = "0";
        await Click(Key("="), () => view.Constant.Text == "UINT32_C(0x00000000)");
        Key("1").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Key("2").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(view.Expression.Text == "12", "number keys append in order and replace selected result");
        await Click(Key("="), () => view.Constant.Text.EndsWith("0000000C)", StringComparison.Ordinal));
        var hex = view.BaseRows.Children.OfType<Button>().Single(button => AutomationProperties.GetName(button) == "HEX 输入进制");
        await Click(hex, () => Key("A").IsEnabled);
        check(view.Expression.Text == "0000000C" || view.Expression.Text == "C", "base switch changes representation without changing value");
        await Click(Key("+"), () => view.Expression.Text.EndsWith(" + ", StringComparison.Ordinal));
        Key("A").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Click(Key("="), () => view.Constant.Text.EndsWith("00000016)", StringComparison.Ordinal));
        var qword = view.WidthButtons.Children.OfType<Button>().Single(button => Equals(button.Content, "QWORD"));
        await Click(qword, () => view.Bits.Children.Count == 64);
        var top = view.Bits.Children.OfType<Button>().Single(button => AutomationProperties.GetName(button) == "bit 63");
        await Click(top, () => view.Constant.Text.Contains("8000000000000016", StringComparison.Ordinal));
        check(view.LittleEndian.Text == "16 00 00 00 00 00 00 80", "QWORD top-bit click remains exact through process protocol");
        await Click(view.FieldMode, () => view.FieldControls.IsVisible);
        view.Expression.Text = "0x12345678";
        view.FieldStart.Text = "8";
        view.FieldCount.Text = "8";
        var extract = PluginDescendants(view.FieldControls).OfType<Button>().Single(button => Equals(button.Tag, "inspect"));
        await Click(extract, () => view.FieldResult.Text.Contains("提取值 86", StringComparison.Ordinal));
        check(view.FieldResult.Text.Contains("掩码 0xFF00", StringComparison.Ordinal), "MCU field tool displays mask and extracted value");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            await Layout();
            check(view.ActualWidth > 700 && view.Keypad.ActualHeight > 250 && view.McuDetails.IsVisible, theme.Id + " calculator has keypad and MCU pane");
            Render(view, Path.Combine(directory, "programmer-calculator-" + theme.Id + ".png"));
        }
        tool.Width = 440;
        tool.Height = 720;
        await Layout();
        check(Grid.GetRow(view.McuDetails) == 1 && view.Expression.ActualWidth > 280, "compact calculator moves MCU tools below keypad without clipping input");
        Render(view, Path.Combine(directory, "programmer-calculator-compact.png"));
        var retained = view.Expression.Text;
        view.Expression.Text = "0xAB + 1";
        retained = view.Expression.Text;
        tool.Close();
        check(!tool.IsVisible, "close button hides independent window");
        await InvokePluginCommandAsync(id, "open", JsonSerializer.SerializeToElement(new
        {
        }));
        await Layout();
        check(tool.IsVisible && ReferenceEquals(view, ((ContentControl)tool.Content).Content) && view.Expression.Text == retained, "manager open restores the same window and calculator state");
        var sample = StudioX.Application.Plugins.PluginProgrammerCalculator.Parse(pluginWorkspace!.LatestPanels[id + "/lab"].Widgets.Single().Value!.Value) with
        {
            RequestId = 0
        };
        var delayed = new ProgrammerCalculatorView(JsonSerializer.SerializeToElement(sample), _ => Task.CompletedTask, PluginManager.Log);
        delayed.Expression.Text = "1";
        delayed.Keypad.Children.OfType<Button>().Single(button => Equals(button.Content, "=")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        delayed.Expression.Text = "23";
        delayed.Update(JsonSerializer.SerializeToElement(sample with
        {
            Expression = "1",
            RequestId = 1
        }));
        check(delayed.Expression.Text == "23", "late process panel cannot overwrite input typed after submission");
        tool.Hide();
        ApplyTheme(ThemeService.Dark);
    }
}
