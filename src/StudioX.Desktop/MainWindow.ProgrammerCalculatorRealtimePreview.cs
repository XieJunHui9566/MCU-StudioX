namespace StudioX.Desktop;

using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using StudioX.Application.Plugins;

public partial class MainWindow
{
    private async Task CheckRealtimeCalculatorAsync(ProgrammerCalculatorView view, string directory, Action<bool, string> check)
    {
        Button Key(string key) => view.Keypad.Children.OfType<Button>().Single(button => Equals(button.Content, key));
        async Task Ready(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            while (!condition())
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                await Task.Delay(10, timeout.Token);
            }
        }
        async Task Click(Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await view.InputIdle;
        }
        view.Expression.Text = "51";
        view.Expression.CaretIndex = 1;
        await Ready(() => view.Constant.Text == "UINT32_C(0x00000033)");
        check(view.Expression.Text == "51" && view.Expression.CaretIndex == 1 && view.Expression.SelectionLength == 0, "typing updates conversion without equals or disturbing caret");
        var hex = view.BaseRows.Children.OfType<Button>().Single(button => AutomationProperties.GetName(button) == "HEX 输入进制");
        await Click(hex);
        view.Expression.Text = "51";
        await Ready(() => view.Constant.Text == "UINT32_C(0x00000051)");
        check(view.IntegerInterpretation.Text.Contains("无符号 81", StringComparison.Ordinal), "HEX 51 converts immediately to DEC 81");
        await Click(Key(">>"));
        check(view.Constant.Text.EndsWith("00000028)", StringComparison.Ordinal), "right-shift key executes one bit without equals");
        await Click(Key("<<"));
        check(view.Constant.Text.EndsWith("00000050)", StringComparison.Ordinal), "left-shift key executes one bit without equals");
        view.Expression.Text = "1 << 0d4";
        await Ready(() => view.Constant.Text.EndsWith("00000010)", StringComparison.Ordinal));
        check(true, "complete typed shift expression updates without equals");
        view.Expression.Text = "1 <<";
        await Ready(() => view.StatusText.Text == "输入中…");
        check(view.Constant.Text.EndsWith("00000010)", StringComparison.Ordinal), "unfinished shift preserves last result without an error");
        view.Expression.Text = "0x";
        await Task.Delay(90);
        await view.InputIdle;
        check(view.StatusText.Text == "输入中…", "incomplete numeric prefix is editable without an error");
        view.Expression.Text = "1/0";
        await Ready(() => view.StatusText.Text.Contains("除数不能为 0", StringComparison.Ordinal));
        await Click(Key("清空"));
        check(view.Constant.Text.EndsWith("00000000)", StringComparison.Ordinal), "clear recovers from a live calculation error");
        view.Expression.Text = "1";
        await Ready(() => view.Constant.Text.EndsWith("00000001)", StringComparison.Ordinal));
        for (var i = 0; i < 10; i++)
        {
            Key("<<").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        await view.InputIdle;
        check(view.Constant.Text.EndsWith("00000400)", StringComparison.Ordinal), "ten rapid shift clicks are serialized without losing shifts");
        var qword = view.WidthButtons.Children.OfType<Button>().Single(button => Equals(button.Content, "QWORD"));
        await Click(qword);
        var bitReference = view.Bits.Children[0];
        var immediate = new List<double>();
        var completed = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var clock = Stopwatch.StartNew();
            Key("清空").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            immediate.Add(clock.Elapsed.TotalMilliseconds);
            check(view.Constant.Text == "UINT64_C(0x0000000000000000)" && view.IsEnabled, "clear displays zero immediately while input stays enabled " + i);
            await view.InputIdle;
            completed.Add(clock.Elapsed.TotalMilliseconds);
        }
        check(ReferenceEquals(bitReference, view.Bits.Children[0]), "calculation and clear reuse existing bit controls");
        check(immediate.Max() < 250 && completed.Max() < 1500, "clear responsiveness has no seconds-long wait");
        var sample = PluginProgrammerCalculator.Parse(pluginWorkspace!.LatestPanels["studiox.bit-lab/lab"].Widgets.Single().Value!.Value) with
        {
            RequestId = 0,
            Preview = false
        };
        var zero = sample with
        {
            Hex = new string('0', sample.Width / 4),
            Binary = new string('0', sample.Width),
            Expression = "0",
            DecimalText = "0"
        };
        var responseGate = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new ProgrammerCalculatorView(JsonSerializer.SerializeToElement(sample), _ => Task.CompletedTask, PluginManager.Log);
        fake.InvokeCalculatorAsync = async request =>
        {
            var id = request.GetProperty("requestId").GetInt32();
            if (id == 1)
            {
                return await responseGate.Task;
            }
            return JsonSerializer.SerializeToElement(new
            {
                ok = true,
                view = zero with
                {
                    RequestId = id
                }
            });
        };
        fake.Expression.Text = "999";
        fake.Keypad.Children.OfType<Button>().Single(button => Equals(button.Content, "=")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var clearClock = Stopwatch.StartNew();
        fake.Keypad.Children.OfType<Button>().Single(button => Equals(button.Content, "清空")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        clearClock.Stop();
        check(fake.Constant.Text.EndsWith(new string('0', sample.Width / 4) + ")", StringComparison.Ordinal) && clearClock.ElapsedMilliseconds < 250 && fake.IsEnabled,
            "clear is immediate even when a prior process response is delayed");
        responseGate.SetResult(JsonSerializer.SerializeToElement(new
        {
            ok = true,
            view = sample with
            {
                RequestId = 1
            }
        }));
        await fake.InputIdle;
        check(fake.Constant.Text.EndsWith(new string('0', sample.Width / 4) + ")", StringComparison.Ordinal), "late result cannot restore the value erased by clear");
        var legacyRequests = new List<string>();
        var legacy = new ProgrammerCalculatorView(JsonSerializer.SerializeToElement(sample with
        {
            Realtime = false
        }), request =>
        {
            legacyRequests.Add(request.GetProperty("action").GetString()!);
            return Task.CompletedTask;
        }, PluginManager.Log);
        legacy.Expression.Text = "7";
        await Task.Delay(70);
        legacy.Keypad.Children.OfType<Button>().Single(button => Equals(button.Content, "<<")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(legacyRequests.Count == 0 && legacy.Expression.Text.EndsWith(" << ", StringComparison.Ordinal), "legacy calculator data keeps its original command contract without live requests");
        await File.WriteAllTextAsync(Path.Combine(directory, "calculator-responsiveness.json"), JsonSerializer.Serialize(new
        {
            samples = immediate.Count,
            immediateMilliseconds = immediate,
            completedMilliseconds = completed,
            immediateMaximum = immediate.Max(),
            completionMaximum = completed.Max(),
            isolatedDelayedResponseClearMilliseconds = clearClock.Elapsed.TotalMilliseconds
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
