namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using StudioX.Application;

public partial class MainWindow
{
    /// <summary>在显式指定的隔离工作区展示第一方配置插件，不执行预览测试动作。</summary>
    public async Task ShowMcuConfigurationAsync(string archive, string project)
    {
        var entry = await services.PluginManager.ImportAsync(archive);
        await services.PluginManager.SetEnabledAsync(entry.Id, true);
        await OpenProjectAsync(project, CancellationToken.None);
        Width = 1500;
        Height = 1100;
        BottomRow.Height = new GridLength(55);
        if (pluginActivities.TryGetValue(entry.Id, out var activity))
        {
            activity.Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }

    /// <summary>隔离用户目录启动真实配置插件，验证图形交互并保存深浅主题截图；不连接硬件。</summary>
    public async Task RenderMcuConfigurationPreviewAsync(string directory, string archive, string project)
    {
        var checks = new List<string>();
        void Check(bool passed, string name)
        {
            if (!passed)
            {
                throw new InvalidOperationException(name);
            }
            checks.Add(name);
        }
        async Task Layout()
        {
            await Task.Delay(100);
            await Dispatcher.InvokeAsync(() => { FlushPluginPanels(); UpdateLayout(); }, DispatcherPriority.ContextIdle);
        }
        PluginMcuConfigurationView View() => pluginPanels.Values.SelectMany(item => PluginDescendants(item.Host)).OfType<PluginMcuConfigurationView>().Single();
        async Task Wait(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            do
            {
                await Layout();
                if (condition())
                {
                    return;
                }
                await Task.Delay(50);
            } while (DateTime.UtcNow < deadline);
            throw new TimeoutException("配置插件界面没有达到预期状态：" + PluginManager.OperationStatus.Text + "\n" + PluginManager.DiagnosticLog.Text +
                "\nloaded=" + View().IsLoaded + ", enabled=" + View().IsEnabled + ", page=" + View().Page +
                "\ncontrols=" + string.Join("; ", PluginDescendants(View()).OfType<ComboBox>().Select(control => AutomationProperties.GetName(control) + ":" + control.Text)) +
                "\nclocks=" + string.Join("; ", View().State.Clocks.Select(node => node.Id + ":" + node.Value + ":" + node.Enabled)));
        }
        Button FindButton(string title) => PluginDescendants(View()).OfType<Button>().Single(button => Equals(button.Content, title));
        void Select(string name, string value)
        {
            foreach (var category in PluginDescendants(View()).OfType<Expander>())
            {
                category.IsExpanded = true;
            }
            View().UpdateLayout();
            var control = PluginDescendants(View()).OfType<ComboBox>().First(combo => AutomationProperties.GetName(combo) == name);
            control.SelectedItem = control.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Tag, value));
        }
        async Task<PluginMcuPinContextMenu> PinContextAsync(string id)
        {
            var button = Diagram().PinAnchor(id);
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
            {
                RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent
            });
            await Wait(() => View().IsEnabled && Diagram().PinAnchor(id).ContextMenu is PluginMcuPinContextMenu { IsOpen: true });
            return (PluginMcuPinContextMenu)Diagram().PinAnchor(id).ContextMenu;
        }
        var entry = await services.PluginManager.ImportAsync(archive);
        Check(!entry.Enabled, "import does not automatically trust plugin");
        await services.PluginManager.SetEnabledAsync(entry.Id, true);
        Check(!pluginActivities.ContainsKey(entry.Id), "no project has no configurator activity");
        await OpenProjectAsync(project, CancellationToken.None);
        Width = 1500;
        Height = 1100;
        BottomRow.Height = new GridLength(55);
        await Wait(() => pluginActivities.ContainsKey(entry.Id) && pluginPanels.Count == 1);
        pluginActivities[entry.Id].Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(() => View().State.CanSave && View().IsEnabled);
        Check(View().State.Pins.Length == 44, "actual plugin provides all physical LQFP44 pins");
        if (View().State.Fields.Any(field => field.Id == "timer2"))
        {
            await RenderExtendedClockPreviewAsync(directory, View, Wait, Layout, Check);
            await File.WriteAllTextAsync(Path.Combine(directory, "checks.txt"), string.Join("\n", checks));
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: " + checks.Count + " extended clock and peripheral UI checks; no hardware.");
            return;
        }
        PluginMcuPackageDiagram Diagram() => PluginDescendants(View()).OfType<PluginMcuPackageDiagram>().Single();
        Check(Diagram().PinBounds("pad-1").X < Diagram().PinBounds("pad-11").X &&
            Diagram().PinBounds("pad-12").Y > Diagram().PinBounds("pad-22").Y &&
            Diagram().PinBounds("pad-23").X > Diagram().PinBounds("pad-33").X &&
            Diagram().PinBounds("pad-34").Y < Diagram().PinBounds("pad-44").Y,
            "STC physical numbering keeps bottom-left origin and all four side directions");
        var physical = PluginDescendants(View()).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "物理引脚 4 P1.0");
        physical.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(() => View().State.SelectedPinId == "pad-4" && Diagram().PinAnchor("pad-4").ContextMenu is { IsOpen: true });
        var menu = Diagram().PinAnchor("pad-4").ContextMenu as PluginMcuPinFunctionMenu;
        Check(menu?.IsOpen == true && !menu.Items.OfType<MenuItem>().Any(item => item.Header is TextBox), "left click opens a compact function menu without an unnecessary search box");
        await Layout();
        Render(menu!, Path.Combine(directory, "pin-menu.png"));
        menu!.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "gpio")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Wait(() => View().State.Pins.Single(pin => pin.Id == "pad-4").FunctionId == "gpio");
        Check(View().State.Dirty, "clicking physical pin and function updates plugin draft");
        Check(!menu.IsOpen, "old function menu closes after plugin state changes");
        Check(!PluginDescendants(View()).OfType<TextBlock>().Any(text => text.Text == "当前引脚") &&
            !PluginDescendants(View()).OfType<ComboBox>().Any(control => AutomationProperties.GetName(control) == "当前引脚功能"), "redundant current-pin sidebar has been removed");
        var context = await PinContextAsync("pad-4");
        Check(context.LockItem.IsEnabled && context.LabelBox.Text == "", "right click opens naming and locking actions instead of the function list");
        Render(context, Path.Combine(directory, "pin-context-menu.png"));
        context.LabelBox.Text = "LED";
        context.ApplyLabelItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Wait(() => View().IsEnabled && View().State.Pins.Single(pin => pin.Id == "pad-4").Label == "LED");
        context = await PinContextAsync("pad-4");
        context.LockItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Wait(() => View().IsEnabled && View().State.Pins.Single(pin => pin.Id == "pad-4").Locked);
        Diagram().PinAnchor("pad-4").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(() => Diagram().PinAnchor("pad-4").ContextMenu is PluginMcuPinFunctionMenu { IsOpen: true });
        var lockedMenu = (PluginMcuPinFunctionMenu)Diagram().PinAnchor("pad-4").ContextMenu;
        Check(!lockedMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "gpio")), "locked pin offers no reassignment or reset actions");
        lockedMenu.Dismiss();
        context = await PinContextAsync("pad-4");
        Check(context.LockItem.IsChecked, "right-click menu reflects the persisted pin lock");
        context.LockItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Wait(() => View().IsEnabled && !View().State.Pins.Single(pin => pin.Id == "pad-4").Locked);
        context = await PinContextAsync("pad-4");
        context.LockItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Wait(() => View().IsEnabled && View().State.Pins.Single(pin => pin.Id == "pad-4").Locked);
        var zoom = PluginDescendants(View()).OfType<Slider>().Single(slider => AutomationProperties.GetName(slider) == "封装图缩放");
        zoom.Value = 1.1;
        View().SelectPage(1);
        await Layout();
        View().SelectPage(0);
        await Layout();
        Check(PluginDescendants(View()).OfType<Slider>().Single().Value == 1.1, "package zoom persists across page changes");
        var viewport = PluginDescendants(View()).OfType<PluginMcuDiagramViewport>().Single();
        var anchor = new Point(160, 180);
        var beforeZoom = Diagram().TransformToAncestor(viewport).Inverse!.Transform(anchor);
        viewport.SetZoom(1.3, anchor);
        var afterZoom = Diagram().TransformToAncestor(viewport).Inverse!.Transform(anchor);
        Check((afterZoom - beforeZoom).Length < 0.01, "zoom keeps the package coordinate under the pointer fixed");
        View().SelectPage(1);
        View().SelectPage(0);
        await Layout();
        viewport = PluginDescendants(View()).OfType<PluginMcuDiagramViewport>().Single();
        Check(viewport.Pan.Length > 0 && viewport.Zoom == 1.3, "viewport translation survives page reconstruction");
        viewport.ResetView();
        PluginDescendants(View()).OfType<Slider>().Single().Value = 0.7;
        View().SelectPage(1);
        await Layout();
        var clockView = CheckClockViewport(View(), Check);
        View().SelectPage(0);
        View().SelectPage(1);
        await Layout();
        Check(PluginDescendants(View()).OfType<PluginMcuDiagramViewport>().Single() is var restoredClock && restoredClock.Zoom == clockView.Zoom && restoredClock.Pan == clockView.Pan, "clock pan and zoom persist across page changes independently of package view");
        Select("下载后使用的硬件时钟源", "IRC");
        Select("源时钟频率 / MHz（分频前）", "24");
        Select("系统时钟分频", "2");
        await Wait(() => View().IsEnabled && View().State.Clocks.Single(node => node.Id == "SYSCLK").Value == "12 MHz");
        Check(PluginDescendants(View()).OfType<PluginMcuDiagramViewport>().Single() is var updatedClock && updatedClock.Zoom == clockView.Zoom && updatedClock.Pan == clockView.Pan, "live clock changes preserve the current viewport");
        Check(View().State.Clocks.Single(node => node.Id == "SOURCE_MUX").ActiveInputIds?.SequenceEqual(["IRC"]) == true, "clock selections update frequency and active path without applying manually");
        Select("系统时钟分频", "4");
        await Wait(() => View().IsEnabled && View().State.Clocks.Single(node => node.Id == "CPU").Value == "6 MHz");
        Select("下载后使用的硬件时钟源", "XTAL");
        await Wait(() => View().IsEnabled && View().State.Clocks.Single(node => node.Id == "XTAL").Enabled);
        Check(!View().State.Clocks.Single(node => node.Id == "IRC").Enabled && View().State.Fields.Single(field => field.Id == "clockMHz").ClockNodeId == "XTAL", "live source changes move frequency input and deactivate the old source");
        Select("下载后使用的硬件时钟源", "IRC");
        Select("系统时钟分频", "2");
        await Wait(() => View().IsEnabled && View().State.Clocks.Single(node => node.Id == "IRC").Enabled && View().State.Clocks.Single(node => node.Id == "CPU").Value == "12 MHz");
        Check(!PluginDescendants(View()).OfType<ComboBox>().Any(control => AutomationProperties.GetName(control) == "串口引脚组"), "clock page contains clock fields without peripheral controls");
        View().SelectPage(0);
        await Layout();
        Check(!PluginDescendants(View()).OfType<PluginMcuClockDiagram>().Any(), "pin and peripheral page has no clock diagram");
        Select("保留 Mon51 串口资源（保护选项）", "none");
        Select("电气模式", "pushPull");
        Select("初始锁存电平", "0");
        Select("串口引脚组", "p36");
        await Wait(() => View().IsEnabled && View().State.Fields.Single(field => field.Id == "uartRoute").Value == "p36");
        Select("计数时钟", "12");
        FindButton("时钟树").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(() => View().IsEnabled && View().Page == 1 && View().State.Clocks.Single(node => node.Id == "T0_CLK").Value == "1 MHz");
        Check(View().State.Clocks.Single(node => node.Id == "T0").Enabled && View().State.Clocks.Single(node => node.Id == "T0_CLK").Symbol == "÷ 12", "timer changes carry into the clock tree even when switching pages immediately");
        View().SelectPage(0);
        await Layout();
        var timer1 = PluginDescendants(View()).OfType<ComboBox>().Last(control => AutomationProperties.GetName(control) == "计数时钟");
        timer1.SelectedItem = timer1.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Tag, "1"));
        await Wait(() => View().IsEnabled && View().State.Clocks.Single(node => node.Id == "T1_CLK").Value == "12 MHz");
        Check(View().State.Pins.Single(pin => pin.Id == "pad-4").Label == "LED" && View().State.Clocks.Single(node => node.Id == "T2").Enabled, "automatic peripheral updates retain pin names and UART configuration");
        Check(View().State.CanGenerate, "exact clock and UART configuration passes plugin rules");
        View().SelectPage(1);
        await Layout();
        var frequency = PluginDescendants(View()).OfType<ComboBox>().Single(control => AutomationProperties.GetName(control) == "源时钟频率 / MHz（分频前）");
        Check(frequency.IsEditable && frequency.Text == "24" && frequency.Items.Count == 9, "frequency combo shows MHz value and nine editable presets");
        frequency.Text = "23.04";
        await Wait(() => View().IsEnabled && View().State.Fields.Single(field => field.Id == "clockMHz").Value == "23.04");
        Check(View().State.CanGenerate && View().State.Clocks.Single(node => node.Id == "SYSCLK").Value == "11.52 MHz", "custom MHz text updates divided clock tree without losing precision");
        frequency = PluginDescendants(View()).OfType<ComboBox>().Single(control => AutomationProperties.GetName(control) == "源时钟频率 / MHz（分频前）");
        frequency.Text = "";
        await Wait(() => View().IsEnabled && View().State.Fields.Single(field => field.Id == "clockMHz").Value == "");
        Check(!View().State.CanGenerate && View().State.Clocks.Single(node => node.Id == "SYSCLK").Value == "尚未设置", "clearing frequency removes previous derived values and blocks generation");
        frequency = PluginDescendants(View()).OfType<ComboBox>().Single(control => AutomationProperties.GetName(control) == "源时钟频率 / MHz（分频前）");
        frequency.Text = "30";
        await Wait(() => View().IsEnabled && PluginDescendants(View()).OfType<TextBlock>().Any(text => text.IsVisible && text.Text.StartsWith("时钟参数未能更新", StringComparison.Ordinal)));
        Check(frequency.Text == "30" && View().State.Fields.Single(field => field.Id == "clockMHz").Value == "" && View().HasChanges, "invalid live frequency retains input, reports stale graph and keeps draft protection");
        Select("源时钟频率 / MHz（分频前）", "24");
        await Wait(() => View().IsEnabled && View().State.Fields.Single(field => field.Id == "clockMHz").Value == "24");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            View().SelectPage(0);
            await Layout();
            foreach (var category in PluginDescendants(View()).OfType<Expander>())
            {
                category.IsExpanded = category.Header.ToString()!.StartsWith("当前 GPIO", StringComparison.Ordinal);
            }
            await Layout();
            var diagramBounds = Diagram().TransformToAncestor(this).TransformBounds(new Rect(Diagram().RenderSize));
            var workspaceBounds = WorkspaceTabs.TransformToAncestor(this).TransformBounds(new Rect(WorkspaceTabs.RenderSize));
            Render(this, Path.Combine(directory, "pins-" + theme.Id + ".png"));
            Check(diagramBounds.Bottom <= workspaceBounds.Bottom, "default zoom keeps all package pins within the visible workspace in " + theme.Id);
            View().SelectPage(1);
            await Layout();
            var clockDiagram = PluginDescendants(View()).OfType<PluginMcuClockDiagram>().Single();
            FindButton("居中适配").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Layout();
            var clockViewport = PluginDescendants(View()).OfType<PluginMcuDiagramViewport>().Single();
            var clockBounds = clockDiagram.TransformToAncestor(clockViewport).TransformBounds(new Rect(clockDiagram.RenderSize));
            Check(clockBounds.Left >= 0 && clockBounds.Top >= 0 && clockBounds.Right <= clockViewport.ActualWidth && clockBounds.Bottom <= clockViewport.ActualHeight, "fit command shows the complete clock diagram in " + theme.Id);
            Check(clockDiagram.Children.OfType<System.Windows.Shapes.Polygon>().Any() && View().State.Clocks.Any(node => node.Kind == "junction"), "clock tree includes source multiplexer and system clock bus in " + theme.Id);
            Render(this, Path.Combine(directory, "clocks-" + theme.Id + ".png"));
            Render(clockDiagram, Path.Combine(directory, "clock-diagram-" + theme.Id + ".png"));
            Check(View().ActualWidth > 600, "configuration view retains useful width in " + theme.Id);
        }
        Check(!PluginDescendants(View()).OfType<Button>().Any(button => Equals(button.Content, "代码预览")), "code preview page and action have been removed");
        var tabs = PluginDescendants(View()).OfType<StackPanel>().Single(panel => panel.Children.OfType<Button>().Any(button => Equals(button.Content, "引脚与外设")));
        Check(tabs.Orientation == Orientation.Horizontal && tabs.Children.Count == 2, "pin/peripheral and clock tabs are arranged horizontally");
        Check(!File.ReadAllText(Path.Combine(project, "device.mcuconfig")).Contains("\"LED\"", StringComparison.Ordinal), "UI edits remain unsaved until explicit save or generate");
        Check(!await ConfirmDocumentsAsync(_ => MessageBoxResult.Cancel), "canceling draft discard preserves open configuration");
        FindButton("保存配置 ●").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(() => !View().State.Dirty && View().IsEnabled);
        Check(File.ReadAllText(Path.Combine(project, "device.mcuconfig")).Contains("\"LED\"", StringComparison.Ordinal), "save button commits configuration without navigating to approval panel");
        FindButton("重新读取").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(() => View().IsEnabled && !View().State.Dirty && View().State.Pins.Single(pin => pin.Id == "pad-4") is { Locked: true, Label: "LED" });
        Check(true, "pin name and lock survive saving and reloading through the real plugin host");
        Check(!File.Exists(Path.Combine(project, "src/generated/mcuconfig/gpio.c")), "save alone does not generate source code");
        FindButton("生成代码").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(() => !View().State.Dirty && File.Exists(Path.Combine(project, "src/generated/mcuconfig/gpio.c")) && View().IsEnabled);
        Check(File.ReadAllText(Path.Combine(project, "src/main.c")).Contains("sx_mcuconfig_init();", StringComparison.Ordinal), "real generation button updates main initialization through application service");
        Check(File.ReadAllText(Path.Combine(project, "src/generated/mcuconfig/gpio.c")).Contains("P1M0", StringComparison.Ordinal) && File.ReadAllText(Path.Combine(project, "src/generated/mcuconfig/uart.c")).Contains("P_SW1", StringComparison.Ordinal), "real UI generates separate GPIO and UART sources");
        View().SelectPage(0);
        await Layout();
        Render(this, Path.Combine(directory, "generated-project.png"));
        var clockValues = View().State.Fields.ToDictionary(field => field.Id, field => field.Value, StringComparer.Ordinal);
        await RenderManagedClockScopePreviewAsync(directory, project, entry.Id, clockValues, Check);
        await File.WriteAllTextAsync(Path.Combine(directory, "checks.txt"), string.Join("\n", checks));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: " + checks.Count + " offline graphical configuration checks; no hardware.");
    }
}
