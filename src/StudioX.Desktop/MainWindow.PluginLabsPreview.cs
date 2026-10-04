namespace StudioX.Desktop;

using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Extensions;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>安装真实实验室包到隔离数据目录，点击竖栏、表单与关闭按钮；不连接硬件。</summary>
    public async Task RenderPluginLabsPreviewAsync(string directory, string archives)
    {
        var checks = new List<string>();
        void Check(bool value, string description)
        {
            if (!value)
            {
                throw new InvalidOperationException(description);
            }
            checks.Add(description);
        }
        async Task Layout()
        {
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        var project = Path.Combine(directory, "fixture-project");
        Directory.CreateDirectory(Path.Combine(project, ".studiox"));
        await JsonStore.WriteAsync(Path.Combine(project, ".studiox/project.json"),
            new ProjectManifest(1, "plugin-labs-preview", "offline.pack", "1.0.0", "offline", "offline", "blank", "offline", "1.0.0", "gcc"));
        try
        {
            foreach (var file in Directory.GetFiles(archives, "*.studioxplugin"))
            {
                var entry = await services.PluginManager.ImportAsync(file);
                Check(!entry.Enabled, entry.Id + " imported without auto-executing code");
                await services.PluginManager.SetEnabledAsync(entry.Id, true);
            }
            projectDirectory = project;
            await ReloadPluginWorkspaceAsync(CancellationToken.None);
            Check(pluginActivities.Count == 4 && PluginActivityRail.Children.Count == 4, "four left-rail plugin icons after real host activation");
            Check(pluginPanels.Count == 4 && PluginManager.PanelsHost.Children.Count == 0, "plugin panels live in their own pages");
            Width = 1450;
            Height = 980;
            BottomRow.Height = new GridLength(80);
            await Layout();
            foreach (var id in new[] { "studiox.bit-lab", "studiox.protocol-lab", "studiox.wave-lab", "studiox.pixel-lab" })
            {
                var activity = pluginActivities[id];
                Check(AutomationProperties.GetName(activity.Button).StartsWith("插件：", StringComparison.Ordinal), id + " accessible named icon");
                activity.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Layout();
                Check(WorkspaceTabs.SelectedItem == activity.Tab && activity.Body.IsVisible, id + " rail click opens corresponding page");
                var oldOutput = PluginDescendants(activity.Body).OfType<TextBox>().Last().Text;
                switch (id)
                {
                    case "studiox.bit-lab":
                        Input(activity.Body, "原始值").Text = "0xFFFFFFFF";
                        break;
                    case "studiox.protocol-lab":
                        Input(activity.Body, "报文").Text = "31 32 33 34 35 36 37 38 39";
                        break;
                    case "studiox.wave-lab":
                        Input(activity.Body, "每周期").Text = "32";
                        break;
                    case "studiox.pixel-lab":
                        var flip = PluginDescendants(activity.Body).OfType<CheckBox>().Single(box => Equals(box.Content, "反色（只反转有效像素，补齐位仍为 0）"));
                        flip.IsChecked = true;
                        break;
                }
                await Calculate(activity.Body);
                Check(PluginDescendants(activity.Body).OfType<TextBox>().Last().Text != oldOutput, id + " form button returns new copyable result from process");
                foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
                {
                    ApplyTheme(theme);
                    await Layout();
                    Check(activity.Button.ActualWidth > 0 && activity.Button.TranslatePoint(new Point(), this).X < 65, id + " icon stays in the requested left rail");
                    Render(this, Path.Combine(directory, id + "-" + theme.Id + ".png"));
                }
                var beforeClose = PluginDescendants(activity.Body).OfType<TextBox>().Select(box => box.Text).ToArray();
                await CloseWorkspaceTabAsync(activity.Tab);
                Check(activity.Tab.Visibility == Visibility.Collapsed && pluginActivities.ContainsKey(id), id + " closing page retains icon");
                activity.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Layout();
                Check(PluginDescendants(activity.Body).OfType<TextBox>().Select(box => box.Text).SequenceEqual(beforeClose), id + " reopening keeps form and result");
                await CloseWorkspaceTabAsync(activity.Tab);
                await InvokePluginCommandAsync(id, "open", JsonSerializer.SerializeToElement(new
                {
                }));
                await Layout();
                Check(WorkspaceTabs.SelectedItem == activity.Tab && activity.Body.IsVisible, id + " manager command opens corresponding page");
            }
            var protocol = pluginActivities["studiox.protocol-lab"];
            protocol.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Layout();
            var successful = PluginDescendants(protocol.Body).OfType<TextBox>().Last().Text;
            Input(protocol.Body, "报文").Text = "GG";
            await Calculate(protocol.Body);
            Check(PluginDescendants(protocol.Body).OfType<TextBlock>().Any(block => block.Text.Contains("输入错误 / Error", StringComparison.Ordinal)), "invalid form input renders visible error");
            Check(PluginDescendants(protocol.Body).OfType<TextBox>().Last().Text == successful, "invalid form preserves last successful output");
            Input(protocol.Body, "报文").Text = "01 03 00 00 00 0A";
            await Calculate(protocol.Body);
            Check(!PluginDescendants(protocol.Body).OfType<TextBlock>().Any(block => block.Text.Contains("输入错误 / Error", StringComparison.Ordinal)), "corrected form clears error");
            Width = 1050;
            Height = 700;
            await Layout();
            var railScroll = PluginDescendants(this).OfType<ScrollViewer>().Single(scroll => scroll.Content is StackPanel panel && panel.Children.Contains(PluginActivityRail));
            Check(railScroll.ScrollableHeight >= 0 && railScroll.ActualHeight > 0, "rail uses a bounded scroll area on compact windows");
            var bodyScroll = (ScrollViewer)protocol.Tab.Content;
            bodyScroll.ScrollToEnd();
            await Layout();
            Check(PluginDescendants(protocol.Body).OfType<TextBox>().Last().ActualWidth > 150, "copy output remains reachable at compact width");
            Render(this, Path.Combine(directory, "plugin-labs-compact.png"));
            // 运行后才创建一个未知 ID、名称和图案的包，证明入口不依赖 IDE 中的插件名单。
            var external = Path.Combine(directory, "external-package");
            ZipFile.ExtractToDirectory(Directory.GetFiles(archives, "*.studioxplugin").First(), external);
            var externalManifestPath = Path.Combine(external, "plugin.json");
            var externalManifest = await PluginManifest.ReadAsync(externalManifestPath);
            externalManifest = externalManifest with
            {
                Id = "validation.external-entry",
                DisplayName = "第三方扩展验证",
                Activity = new(Title: "自定义入口", Tooltip: "由插件声明的提示", Icon: new([[3, 12, 12, 3, 21, 12, 12, 21, 3, 12], [7, 12, 17, 12]]))
            };
            await JsonStore.WriteAsync(externalManifestPath, externalManifest);
            var externalArchive = Path.Combine(directory, "external-entry.studioxplugin");
            await PluginRepository.PackAsync(external, externalArchive);
            await services.PluginManager.ImportAsync(externalArchive);
            await services.PluginManager.SetEnabledAsync(externalManifest.Id, true);
            await ReloadPluginWorkspaceAsync(CancellationToken.None);
            var externalActivity = pluginActivities[externalManifest.Id];
            Check(pluginActivities.Count == 5 && AutomationProperties.GetName(externalActivity.Button) == "插件：自定义入口", "unknown plugin registers its own title without host recompilation");
            Check(externalActivity.Button.Content is PluginActivityIconView && Equals(externalActivity.Button.ToolTip, "由插件声明的提示"), "unknown plugin supplies its own vector icon and tooltip");
            externalActivity.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Layout();
            Check(externalActivity.Tab.IsSelected && externalActivity.Body.Children.Count > 0, "unknown plugin entry opens a working panel");
            Render(this, Path.Combine(directory, "external-plugin-entry.png"));
            await services.PluginManager.SetEnabledAsync(externalManifest.Id, false);
            await ReloadPluginWorkspaceAsync(CancellationToken.None);
            Check(!pluginActivities.ContainsKey(externalManifest.Id) && pluginActivities.Count == 4, "unknown plugin entry is removed when disabled");
            await services.PluginManager.SetEnabledAsync("studiox.protocol-lab", false);
            await ReloadPluginWorkspaceAsync(CancellationToken.None);
            Check(pluginActivities.Count == 3 && !pluginActivities.ContainsKey("studiox.protocol-lab") && PluginActivityRail.Children.Count == 3, "disabling plugin removes its icon and page");
            var oldWorkspace = pluginWorkspace!;
            await StopPluginWorkspaceAsync();
            Check(pluginActivities.Count == 0 && PluginActivityRail.Children.Count == 0 && pluginPanels.Count == 0, "closing workspace clears all plugin pages and icons");
            Check(oldWorkspace.Contributions.All(active => !oldWorkspace.IsPluginRunning(active.Id)), "workspace shutdown stops real plugin processes");
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), $"PASS {checks.Count}\n" + string.Join("\n", checks));
        }
        finally
        {
            await StopPluginWorkspaceAsync();
            projectDirectory = null;
        }
        async Task Calculate(StackPanel body)
        {
            var button = PluginDescendants(body).OfType<Button>().Single(item => Equals(item.Content, "计算 / 生成"));
            var host = body.Children.OfType<ContentControl>().Single();
            var previousPanel = host.Content;
            Check(button.IsEnabled, "calculate button enabled");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pluginInvocationTask;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            do
            {
                await Layout();
                FlushPluginPanels();
                await Layout();
                if (!ReferenceEquals(previousPanel, host.Content))
                {
                    break;
                }
                await Task.Delay(20, timeout.Token);
            } while (true);
        }
        static TextBox Input(StackPanel body, string label) => PluginDescendants(body).OfType<StackPanel>()
            .Where(panel => panel.Children.Count == 2 && panel.Children[0] is TextBlock block && block.Text.StartsWith(label, StringComparison.Ordinal))
            .SelectMany(panel => panel.Children.OfType<TextBox>()).Single();
    }
}
