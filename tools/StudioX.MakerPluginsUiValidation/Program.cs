using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StudioX.Application.Plugins;
using StudioX.Extensions.Abstractions;
using StudioX.MakerPlugins;

internal static class Program
{
    private static readonly List<string> Checks = [];
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is not [var output])
        {
            throw new ArgumentException("Usage: <new-output>");
        }
        output = Path.GetFullPath(output);
        if (Directory.Exists(output))
        {
            throw new ArgumentException("New output directory required.");
        }
        Directory.CreateDirectory(output);
        // 直接复用既有 Desktop DLL 的资源和渲染器，未启动 App.OnStartup 或用户工作区。
        var app = new StudioX.Desktop.App();
        app.InitializeComponent();
        var pid = new Surface(new PidStudioPlugin());
        pid.Screenshot(Path.Combine(output, "pid-tuning.png"));
        pid.Text("kp").Text = "3.5";
        pid.Select("选择页面", "plant");
        pid.Click("保存参数并切换页面");
        Check(pid.Panel.Widgets.Any(w => w.Id == "dirty"), "renderer submits edited Kp and marks uncomputed changes");
        pid.Select("选择页面", "tuning");
        pid.Click("保存参数并切换页面");
        Check(pid.Text("kp").Text == "3.5", "real TextBox retains edited gain after page round trip");
        pid.Text("kp").Text = "-1";
        pid.Click("运行仿真");
        Check(pid.Panel.Widgets.Any(w => w.Id == "error"), "invalid numeric value shows error without losing panel");
        pid.Text("kp").Text = "2";
        pid.Click("运行仿真");
        Check(pid.Panel.Widgets.Any(w => w.Id == "metrics"), "simulate button displays curves and metrics");
        pid.Screenshot(Path.Combine(output, "pid-response.png"));
        pid.Screenshot(Path.Combine(output, "pid-metrics.png"), offset: 680);
        pid.Click("将本次已完成试验");
        Check(pid.Panel.Widgets.Any(w => w.Id == "compare"), "baseline button works through existing renderer");
        pid.Select("选择页面", "tuning");
        pid.Click("保存参数并切换页面");
        pid.Select("载入预设", "load");
        pid.Click("载入所选预设");
        Check(pid.Panel.Widgets.Any(w => w.Id == "comparisonhint"), "changed load scenario labels incomparable baseline conditions");
        pid.Select("选择页面", "plant");
        pid.Click("保存参数并切换页面");
        Check(pid.Text("seconds").Text == "12", "preset replaces persisted host form values");
        pid.Select("选择页面", "limits");
        pid.Click("保存参数并切换页面");
        Check(pid.Text("disturbance").Text == "-0.4", "preset load value appears on separate model page");
        pid.Select("选择页面", "export");
        pid.Click("保存参数并切换页面");
        Check(pid.TextPrefix("code_").Text.Contains("pid_step", StringComparison.Ordinal), "C export displays full controller");
        Check(pid.TextPrefix("csv_").Text.Split('\n').Length == 41, "CSV summary is explicit forty-row data");
        pid.Screenshot(Path.Combine(output, "pid-export.png"));
        pid.Select("选择页面", "tuning");
        pid.Click("保存参数并切换页面");
        pid.Screenshot(Path.Combine(output, "pid-compact.png"), width: 520, height: 780);
        Check(pid.Diagnostics.Count == 0, "no renderer diagnostics from PID user flows");
        var makers = new (string Name, Surface Ui)[] { ("rgb", new(new RgbStudioPlugin())), ("melody", new(new MelodyStudioPlugin())), ("debounce", new(new DebounceStudioPlugin())) };
        foreach (var (name, ui) in makers)
        {
            var previous = ui.TextPrefix("output_").Text;
            if (name == "rgb")
            {
                ui.Text("brightness").Text = "100";
            }
            else if (name == "melody")
            {
                ui.Text("bpm").Text = "60";
            }
            else
            {
                ui.Text("debounceMs").Text = "40";
            }
            ui.Click("计算 / 生成");
            Check(ui.TextPrefix("output_").Text != previous, name + " calculation updates visible copy output");
            ui.Screenshot(Path.Combine(output, name + ".png"));
            Check(ui.Diagnostics.Count == 0, name + " real renderer completes without diagnostics");
        }
        app.Resources["Text"] = Brushes.Black;
        app.Resources["Background"] = Brushes.White;
        app.Resources["Surface"] = Brushes.WhiteSmoke;
        app.Resources["Border"] = Brushes.Gray;
        pid.Screenshot(Path.Combine(output, "pid-light.png"));
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        {
            status = "passed",
            checks = Checks,
            hostAssembly = typeof(StudioX.Desktop.App).Assembly.FullName,
            mode = "In-process existing WPF renderer; native desktop input could not activate window",
            hardwareConnected = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {Checks.Count} WPF renderer checks.");
    }
    private static void Check(bool good, string name)
    {
        if (!good)
        {
            throw new InvalidOperationException(name);
        }
        Checks.Add(name);
        Console.WriteLine("PASS " + name);
    }

    private sealed class Surface
    {
        private readonly object renderer;
        private readonly Type rendererType;
        private FrameworkElement content = null!;
        private Task pending = Task.CompletedTask;
        public readonly List<string> Diagnostics = [];
        public PluginPanelDefinition Panel = null!;
        public Surface(IStudioXPlugin plugin)
        {
            rendererType = typeof(StudioX.Desktop.App).Assembly.GetType("StudioX.Desktop.PluginPanelRenderer", true)!;
            Func<string, JsonElement, Task> invoke = (id, values) => pending = plugin.InvokeAsync("command", id, values, CancellationToken.None);
            renderer = Activator.CreateInstance(rendererType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [invoke, (Action<string>)(s => Diagnostics.Add(s))], null)!;
            var commands = plugin.Describe().Commands.Select(c => c.Id).ToHashSet();
            plugin.ActivateAsync(new FakeHost(panel =>
            {
                PluginContributionValidator.ValidatePanel(panel, commands);
                Panel = panel;
                content = (FrameworkElement)rendererType.GetMethod("Render")!.Invoke(renderer, [panel])!;
                Layout(content, 1100, 1800);
            }), CancellationToken.None).GetAwaiter().GetResult();
        }
        private Dictionary<string, TextBox> Inputs => (Dictionary<string, TextBox>)rendererType.GetField("inputs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
        public TextBox Text(string field) => Inputs.Single(pair => pair.Key == field || pair.Key.EndsWith("_" + field, StringComparison.Ordinal)).Value;
        public TextBox TextPrefix(string prefix) => Inputs.Single(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)).Value;
        public void Select(string label, string value)
        {
            var box = Elements(content).OfType<ComboBox>().Single(c => c.Parent is StackPanel p && p.Children.OfType<TextBlock>().Any(t => t.Text.StartsWith(label, StringComparison.Ordinal)));
            box.SelectedItem = box.Items.Cast<object>().Single(o => ((JsonElement)o.GetType().GetProperty("Value")!.GetValue(o)!).GetString() == value);
        }
        public void Click(string prefix)
        {
            Elements(content).OfType<Button>().Single(b => Convert.ToString(b.Content)!.StartsWith(prefix, StringComparison.Ordinal)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            while (!pending.IsCompleted)
            {
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            }
            pending.GetAwaiter().GetResult();
            Layout(content, 1100, 1800);
        }
        public void Screenshot(string path, int width = 1100, int height = 980, double offset = 0)
        {
            var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var root = new Border { Background = (Brush)System.Windows.Application.Current.Resources["Background"], Child = scroll, Padding = new Thickness(14) };
            Layout(root, width, height);
            scroll.ScrollToVerticalOffset(offset);
            root.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path))
            {
                encoder.Save(file);
            }
            scroll.Content = null;
        }
        private static void Layout(FrameworkElement element, int width, int height)
        {
            element.Measure(new Size(width, height));
            element.Arrange(new Rect(0, 0, width, height));
            element.UpdateLayout();
        }
        private static IEnumerable<DependencyObject> Elements(DependencyObject root)
        {
            yield return root;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                foreach (var child in Elements(VisualTreeHelper.GetChild(root, i)))
                {
                    yield return child;
                }
            }
        }
    }
    private sealed class FakeHost(Action<PluginPanelDefinition> publish) : IPluginHost
    {
        public Task PublishPanelAsync(PluginPanelDefinition panel, CancellationToken cancellationToken)
        {
            publish(panel);
            return Task.CompletedTask;
        }
        public Task<JsonElement> CallAsync(string tool, JsonElement arguments, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected host tool");
        public Task LogAsync(string level, string message, CancellationToken cancellationToken) => throw new InvalidOperationException(level + ": " + message);
    }
}
