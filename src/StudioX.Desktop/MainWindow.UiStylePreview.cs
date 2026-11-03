namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>在隔离用户目录验证真实控件与弹窗；不打开工程、连接硬件或测试 MCP 服务。</summary>
    public async Task RenderUiStylePreviewAsync(string directory)
    {
        var checks = new List<string>();
        void Check(bool value, string label)
        {
            if (!value)
            {
                throw new InvalidOperationException(label);
            }
            checks.Add(label);
        }
        var originalFont = System.Windows.Application.Current.Resources["WorkbenchFontFamily"];
        var originalSize = System.Windows.Application.Current.Resources["WorkbenchFontSize"];
        var custom = ThemeService.Dark with
        {
            Id = "studiox.style-audit",
            DisplayName = "Style audit",
            Colors = new(ThemeService.Dark.Colors)
            {
                ["Accent"] = "#B8E986",
                ["OnAccent"] = "#163020"
            }
        };
        var cases = new (string Name, Func<Window> Create)[]
        {
            ("controls", CreateGallery),
            ("pic-memory", () => new PicMemoryWriteWindow("0x20")),
            ("pic-programmers", () => new PicDownloadWindow(new(), [new("AUDIT-A"), new("AUDIT-B")], () => throw new InvalidOperationException("UI audit must not enumerate USB"))),
            ("symbol-rename", () => new SymbolRenameDialog("sensor_temperature")),
            ("file-name", () => new EntryNameWindow("文件名称", "请输入新的文件名称，修改后可继续在工程中编辑。", "main.c", false)),
            ("ai-mcp", () => new AiMcpServerWindow(services.AiMcp))
        };
        try
        {
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light, custom })
            {
                ApplyTheme(theme);
                foreach (var (name, create) in cases)
                {
                    System.Windows.Application.Current.Resources["WorkbenchFontFamily"] = new FontFamily("Microsoft YaHei UI");
                    System.Windows.Application.Current.Resources["WorkbenchFontSize"] = 13d;
                    var dialog = create();
                    dialog.Owner = this;
                    dialog.ShowInTaskbar = false;
                    dialog.ShowActivated = false;
                    try
                    {
                        dialog.Show();
                        await Settle();
                        Check(dialog.FontSize == 13 && dialog.FontFamily.Source == "Microsoft YaHei UI" && dialog.UseLayoutRounding,
                            theme.Id + "/" + name + " inherits shared typography and layout rounding");
                        var primary = Descendants(dialog).OfType<Button>().Single(b => b.IsDefault);
                        Check(ColorOf(primary.Background) == theme.Colors["Accent"] && ColorOf(primary.Foreground) == theme.Colors["OnAccent"],
                            theme.Id + "/" + name + " default action follows Accent / OnAccent");
                        Save(dialog, theme.Id + "-" + name + ".png");
                        System.Windows.Application.Current.Resources["WorkbenchFontSize"] = 18d;
                        System.Windows.Application.Current.Resources["WorkbenchFontFamily"] = new FontFamily("Segoe UI, Microsoft YaHei UI");
                        await Settle();
                        Check(dialog.FontSize == 18 && primary.FontSize == 18 && dialog.FontFamily.Source == "Segoe UI, Microsoft YaHei UI",
                            theme.Id + "/" + name + " already-open window follows font changes");
                        var bounds = primary.TransformToAncestor(dialog).TransformBounds(new Rect(primary.RenderSize));
                        Check(bounds.Top >= 0 && bounds.Bottom <= dialog.ActualHeight && bounds.Left >= 0 && bounds.Right <= dialog.ActualWidth,
                            theme.Id + "/" + name + " primary action remains inside enlarged dialog");
                        var clipped = Descendants(dialog).OfType<TextBlock>().Where(t => t.IsVisible && t.Text.Length > 0 && t.TextWrapping == TextWrapping.NoWrap &&
                            t.TextTrimming == TextTrimming.None && t.ActualWidth > 0 && t.DesiredSize.Width > t.ActualWidth + 1).Select(t => t.Text).ToArray();
                        Check(clipped.Length == 0, theme.Id + "/" + name + " visible labels are not clipped: " + string.Join(" / ", clipped));
                        Save(dialog, theme.Id + "-" + name + "-large.png");
                        if (name == "controls")
                        {
                            var password = Descendants(dialog).OfType<PasswordBox>().Single();
                            Check(ColorOf(password.Background) == theme.Colors["Background"] && ColorOf(password.Foreground) == theme.Colors["Text"],
                                theme.Id + " password field follows shared input colors");
                            var bubble = Descendants(dialog).OfType<Grid>().Single(g => Equals(g.Tag, "user-bubble"));
                            Check(Descendants(bubble).OfType<TextBlock>().All(t => ColorOf(t.Foreground) == theme.Colors["OnAccent"]),
                                theme.Id + " AI user text follows OnAccent");
                        }
                    }
                    finally { dialog.Close(); }
                }
            }
            await JsonStore.WriteAsync(Path.Combine(directory, "result.json"), new
            {
                passed = true,
                hardware = false,
                paidApi = false,
                themes = 3,
                dialogs = cases.Length,
                screenshots = cases.Length * 3 * 2,
                checks
            });
        }
        finally
        {
            System.Windows.Application.Current.Resources["WorkbenchFontFamily"] = originalFont;
            System.Windows.Application.Current.Resources["WorkbenchFontSize"] = originalSize;
            ApplyTheme(ThemeService.Dark);
        }

        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(50);
        }
        void Save(Window window, string name)
        {
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, name));
            png.Save(file);
        }
    }

    private static Window CreateGallery()
    {
        var panel = new StackPanel { Margin = new(22) };
        panel.Children.Add(new TextBlock { Text = "工作台公共控件", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 16) });
        panel.Children.Add(new TextBlock { Text = "普通输入" });
        panel.Children.Add(new TextBox { Text = "项目路径与中文说明 / project", Margin = new(0, 6, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "密码输入" });
        panel.Children.Add(new PasswordBox { Password = "local-ui-fixture", Margin = new(0, 6, 0, 12) });
        panel.Children.Add(new ComboBox { ItemsSource = new[] { "器件与模板", "开发环境组件" }, SelectedIndex = 0, Margin = new(0, 0, 0, 12) });
        panel.Children.Add(new CheckBox { Content = "启用选项", IsChecked = true, Margin = new(0, 0, 0, 8) });
        panel.Children.Add(new CheckBox { Content = "不可用选项", IsEnabled = false, Margin = new(0, 0, 0, 12) });
        foreach (var (label, resource) in new[] { ("错误：保留原始诊断", "DiagnosticError"), ("警告：请检查设置", "DiagnosticWarning"), ("成功：操作已完成", "DiagnosticSuccess") })
        {
            var text = new TextBlock { Text = label, Margin = new(0, 0, 0, 6) };
            text.SetResourceReference(TextBlock.ForegroundProperty, resource);
            panel.Children.Add(text);
        }
        var bubble = AiTranscriptRenderer.CreateBubble("你", "自定义强调色下，聊天文字也使用正确的对比色。");
        bubble.Tag = "user-bubble";
        panel.Children.Add(bubble);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 10, 0, 0) };
        actions.Children.Add(new Button { Content = "不可用", IsEnabled = false, Margin = new(0, 0, 8, 0) });
        actions.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new(0, 0, 8, 0) });
        actions.Children.Add(new Button { Content = "主动作", IsDefault = true });
        panel.Children.Add(actions);
        return new Window { Title = "UI 风格核对", Width = 660, SizeToContent = SizeToContent.Height, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner };
    }

    private static string ColorOf(Brush brush) => brush is SolidColorBrush solid ? "#" + solid.Color.R.ToString("X2") + solid.Color.G.ToString("X2") + solid.Color.B.ToString("X2") : "";

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i)))
            {
                yield return child;
            }
        }
    }
}
