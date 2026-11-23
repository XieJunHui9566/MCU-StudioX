namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    public async Task RenderAg32UartPreviewAsync(string output, string archive, bool layoutOnly)
    {
        var checks = new List<string>();
        void Check(bool ok, string name)
        {
            if (!ok)
            {
                throw new InvalidOperationException(name + " · " + Status.Text);
            }
            checks.Add(name);
            File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
        }
        async Task ClickAsync(Button button)
        {
            Check(button.IsEnabled && button.IsVisible, button.Content + " 可操作");
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            await Dispatcher.InvokeAsync(() =>
            {
            }, DispatcherPriority.ApplicationIdle);
            await pendingOperation;
        }
        var pack = await services.Packs.ImportAsync(archive);
        foreach (var profile in Ag32DeviceCatalog.All.DistinctBy(profile => profile.PackageName))
        {
            var diagram = new Ag32PackageDiagram();
            diagram.SetPackage(profile.DeviceId, profile.PackageName);
            var perSide = profile.PinCount / 4;
            var pins = Enumerable.Range(1, profile.PinCount).Select(n => new Ag32PackagePinVisual(n, true,
                (n - 1) % perSide == 0 ? "GPIO4_4" : null)).ToArray();
            diagram.SetPins(pins);
            var bounds = new Rect(0, 0, diagram.Width, diagram.Height);
            diagram.Measure(bounds.Size);
            diagram.Arrange(bounds);
            var pads = diagram.Children.OfType<Button>().OrderBy(pad => (int)pad.Tag).ToArray();
            Check(pads.Length == profile.PinCount && Canvas.GetTop(pads[0]) < Canvas.GetTop(pads[perSide - 1]) &&
                Canvas.GetLeft(pads[perSide]) < Canvas.GetLeft(pads[2 * perSide - 1]) &&
                Canvas.GetTop(pads[2 * perSide]) > Canvas.GetTop(pads[3 * perSide - 1]) &&
                Canvas.GetLeft(pads[3 * perSide]) > Canvas.GetLeft(pads[^1]) &&
                diagram.Children.OfType<TextBlock>().Where(label => label.Text.Length > 0).All(label =>
                    bounds.Contains(label.TransformToAncestor(diagram).TransformBounds(new Rect(label.RenderSize)))),
                profile.PackageName + " 四边编号顺序正确，外置标签完整");
        }
        var view = Ag32PinMapping.Planner;
        foreach (var template in new[] { "minimal", "freertos-mcu" })
        {
            var root = Path.Combine(output, template);
            await services.Projects.CreateAsync(pack, "AG32VF303CCT6", template, "uart_demo", root);
            var main = Path.Combine(root, "src", "main.c");
            var entry = await File.ReadAllTextAsync(main);
            Check(entry.Contains("/* STUDIOX GENERATED BEGIN Init */") && entry.Contains("/* STUDIOX GENERATED END Includes */"),
                "新工程在配置外设前已显示自动生成区边界");
            entry = entry.Replace("/* USER CODE BEGIN Includes */", "/* USER CODE BEGIN Includes */\n#include <limits.h>")
                .Replace("/* USER CODE BEGIN BeforeInit */", "/* USER CODE BEGIN BeforeInit */\n    /* 用户初始化前 */")
                .Replace("/* USER CODE BEGIN AfterInit */", "/* USER CODE BEGIN AfterInit */\n    (void)sizeof(int);");
            await File.WriteAllTextAsync(main, entry);
            await File.AppendAllTextAsync(main, "\n/* 用户业务代码 */\nbool App_ReadByte(uint8_t *value) { return StudioX_UART0_TryRead(value); }\n");
            var original = await File.ReadAllBytesAsync(main);
            await OpenProjectAsync(root, CancellationToken.None);
            await OpenSourceAsync("src/main.c", CancellationToken.None);
            await ClickAsync(Ag32PinMappingRailButton);
            Check(view.PinTab.IsSelected && !view.HasChanges, "从左侧工具栏进入，查看配置不创建草稿");
            Check(!view.PinEditor.IsVisible && view.PinLegend.IsVisible, "未选择引脚时显示状态图例，留出封装操作空间");
            if (template == "minimal")
            {
                Render(this, Path.Combine(output, "pin-entry.png"));
            }
            var padsByPin = view.PackageDiagram.Children.OfType<Button>().ToDictionary(button => (int)button.Tag);
            var fixedPins = view.Snapshot!.Pins.Where(pin => pin.FixedPin is not null).ToArray();
            Check(fixedPins.Length == 8 && fixedPins.All(pin => !pin.CanAssign) &&
                Equals(padsByPin[24].Background, Ag32PackageDiagram.SupplyPadBrush) &&
                Equals(padsByPin[23].Background, Ag32PackageDiagram.GroundPadBrush) &&
                Equals(padsByPin[10].Background, Ag32PackageDiagram.AvailablePadBrush),
                "LQFP48 电源、地与可分配脚颜色独立，八个固定脚不能分配");
            await ClickAsync(padsByPin[8]);
            Check(view.PinDetails.Text.Contains("VSSA") && view.PinDetails.Text.Contains("模拟地") &&
                view.ActivePinMenu is { } fixedMenu && !fixedMenu.Items.OfType<MenuItem>().Any(item => item.Tag is Ag32PinFunction) &&
                !view.HasChanges, "模拟地可查看手册管脚名与用途，不能创建映射");
            view.ActivePinMenu!.IsOpen = false;
            await Dispatcher.InvokeAsync(() =>
            {
            }, DispatcherPriority.ApplicationIdle);
            async Task AssignGpioAsync(int number, string function)
            {
                var button = view.PackageDiagram.Children.OfType<Button>().Single(button => Equals(button.Tag, number));
                await ClickAsync(button);
                var choice = view.ActivePinMenu!.Items.OfType<MenuItem>().Single(item => item.Tag is Ag32PinFunction f && f.Name == function);
                ((IInvokeProvider)new MenuItemAutomationPeer(choice).GetPattern(PatternInterface.Invoke)).Invoke();
                await Dispatcher.InvokeAsync(() =>
                {
                }, DispatcherPriority.ApplicationIdle);
            }
            await AssignGpioAsync(10, "GPIO4_4");
            view.PinDirection.SelectedValue = "OUTPUT";
            view.PinLevel.SelectedIndex = 1;
            view.PinSlew.IsChecked = true;
            view.PinPull.SelectedValue = "HOLD";
            Check(view.PinOutputFields.IsVisible && !view.PinInputFields.IsVisible &&
                view.GetAssignments().Any(pin => pin is { InitialHigh: true, SlewControl: true, Pull: "HOLD" }), "GPIO 输出初始高、总线保持和转换速率进入草稿");
            if (template == "minimal")
            {
                UpdateLayout();
                Render(this, Path.Combine(output, "gpio-output.png"));
            }
            await AssignGpioAsync(11, "GPIO4_5");
            view.PinDirection.SelectedValue = "INPUT";
            view.PinInterrupt.SelectedValue = "BOTHEDGE";
            view.PinFilter.Text = "PIN_11";
            Check(view.AssignmentsGrid.Items.Count == 1 && view.PinInterruptHint.Text.Contains("GPIO4_5_InterruptCount"), "GPIO 输入双边沿可见事件计数，筛选定位到目标脚");
            view.PinFilter.Clear();
            view.UartPort.SelectedItem = "UART0";
            Check(view.PinTab.IsSelected && view.UartInspector.IsVisible && view.PackageDiagram.IsVisible,
                "同一引脚分配页同时显示 UART 参数与封装图");
            view.UartEnabled.IsChecked = true;
            Check(!view.SavePlanButton.IsEnabled && view.UartStatus.Text.Contains("引脚"), "未选引脚时就地给出修复提示");
            view.UartTx.SelectedValue = 2;
            view.UartIrq.IsChecked = true;
            Check(!view.SavePlanButton.IsEnabled && view.UartStatus.Text.Contains("RX"), "接收中断要求 RX");
            view.UartRx.SelectedValue = 21;
            view.UartFrame.IsExpanded = true;
            view.UartDataBits.SelectedItem = 7;
            view.UartParity.SelectedValue = "EVEN";
            view.UartStopBits.SelectedItem = 2;
            view.UartMode.SelectedValue = "TX";
            Check(!view.SavePlanButton.IsEnabled && view.UartStatus.Text.Contains("接收方向"), "仅发送时指出接收中断的方向冲突");
            view.UartIrq.IsChecked = false;
            Check(view.UartPreview.Text.Contains("~UART_CR_RXE"), "已映射 RX 时也能显式关闭接收器");
            view.UartMode.SelectedValue = "TX_RX";
            view.UartIrq.IsChecked = true;
            Check(view.UartPreview.Text.Contains("UART_LCR_PARITY_EVEN") && view.UartFrame.Header.ToString()!.Contains("7E2"), "7E2 帧格式与初始化预览一致");
            if (template == "minimal")
            {
                UpdateLayout();
                Render(this, Path.Combine(output, "uart-frame.png"));
            }
            view.UartFrame.IsExpanded = false;
            view.UartAdvanced.IsExpanded = true;
            view.UartFlow.SelectedValue = "RTS_CTS";
            Check(!view.SavePlanButton.IsEnabled && view.UartStatus.Text.Contains("RTS"), "开启双向流控时指出缺失的握手引脚");
            view.UartRts.SelectedValue = 12;
            view.UartCts.SelectedValue = 13;
            view.UartFifo.IsChecked = false;
            view.UartPriority.SelectedItem = 4;
            Check(view.SavePlanButton.IsEnabled && view.UartPreview.Text.Contains("UART_EnableCTS(UART0)") &&
                view.UartPreview.Text.Contains("UART_EnableRTS(UART0)") && view.UartPreview.Text.Contains("UART_LCR_FIFO_1)") &&
                view.UartPreview.Text.Contains("INT_EnableIRQ(UART0_IRQn, 4)"), "两路流控、FIFO 和 PLIC 优先级生成正确");
            if (template == "minimal")
            {
                UpdateLayout();
                view.UartCts.BringIntoView();
                UpdateLayout();
                Render(this, Path.Combine(output, "uart-flow.png"));
            }
            view.UartAdvanced.IsExpanded = false;
            view.UartPort.SelectedItem = "引脚属性";
            var rxButton = view.PackageDiagram.Children.OfType<Button>().Single(button => Equals(button.Tag, 21));
            await ClickAsync(rxButton);
            Check(Equals(view.UartPort.SelectedItem, "UART0") && view.ActivePinMenu?.IsOpen == true,
                "点击已分配的 RX 脚直接定位 UART0 并保留引脚菜单");
            view.ActivePinMenu!.IsOpen = false;
            await Dispatcher.InvokeAsync(() =>
            {
            }, DispatcherPriority.ApplicationIdle);
            view.UartBaud.Text = "oops";
            Check(!view.SavePlanButton.IsEnabled && view.UartStatus.Text.Contains("正整数"), "波特率输入错误立即提示");
            view.UartPort.SelectedItem = "UART1";
            view.UartPort.SelectedItem = "UART0";
            Check(view.UartBaud.Text == "oops" && view.UartIrq.IsChecked == true && Equals(view.UartRx.SelectedValue, 21),
                "切换实例保留无效输入和引脚草稿");
            view.UartBaud.Text = "9999999";
            Check(!view.SavePlanButton.IsEnabled && view.UartStatus.Text.Contains("bit/s"), "波特率超出时钟能力显示可用范围");
            view.UartBaud.Text = "115200";
            Check(view.UartPreview.Text.Contains("115200") && view.SavePlanButton.IsEnabled, "修正后预览与保存恢复");
            if (template == "minimal")
            {
                BottomTabs.SelectedItem = ProjectTerminalTab;
                BottomRow.Height = new GridLength(260);
                foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
                {
                    ApplyTheme(theme);
                    Width = 1460;
                    Height = 900;
                    view.SavePlanButton.BringIntoView();
                    UpdateLayout();
                    await Dispatcher.InvokeAsync(() =>
                    {
                    }, DispatcherPriority.ApplicationIdle);
                    Check(view.PeripheralList.IsVisible && Equals(view.PeripheralList.SelectedItem, "UART0"), "宽窗显示外设列表并同步选中串口");
                    view.PeripheralList.SelectedItem = "UART1";
                    Check(Equals(view.UartPort.SelectedItem, "UART1"), "外设列表切换参数");
                    view.PeripheralList.SelectedItem = "UART0";
                    await Dispatcher.InvokeAsync(() =>
                    {
                    }, DispatcherPriority.ApplicationIdle);
                    UpdateLayout();
                    Check(Equals(view.UartTx.SelectedValue, 2) && Equals(view.UartRx.SelectedValue, 21) &&
                        view.UartTx.Text == "PIN_2" && view.UartRx.Text == "PIN_21", "切回串口后两路引脚标签均可见");
                    var caption = view.PackageDiagram.Children.OfType<TextBlock>().Single(label => Equals(label.Tag, 2));
                    var pad = view.PackageDiagram.Children.OfType<Button>().Single(button => Equals(button.Tag, 2));
                    Check(caption.Text == "UART0_TX" && Canvas.GetLeft(caption) + caption.ActualWidth <= Canvas.GetLeft(pad) + 1 &&
                        ((TextBlock)pad.Content).Text == "2", "封装脚内显示编号，左侧功能名完全位于脚外");
                    Check(view.DiagramZoom.Value >= 90 && ProjectTerminalTab.IsSelected && BottomRow.ActualHeight >= 250,
                        "终端展开 260 像素时封装脚号保持可读比例");
                    Render(this, Path.Combine(output, theme.Id + ".png"));
                }
                ApplyTheme(ThemeService.Dark with
                {
                    Colors = new(ThemeService.Dark.Colors)
                    {
                        ["Accent"] = "#B8E986",
                        ["OnAccent"] = "#163020"
                    }
                });
                System.Windows.Application.Current.Resources["WorkbenchFontSize"] = 18d;
                Width = 900;
                Height = 650;
                BottomRow.Height = new GridLength(150);
                UpdateLayout();
                view.SavePlanButton.BringIntoView();
                await Dispatcher.InvokeAsync(() =>
                {
                }, DispatcherPriority.ApplicationIdle);
                var point = view.SavePlanButton.TranslatePoint(new Point(0, 0), this);
                Check(point.X >= 0 && point.Y >= 0 && point.X + view.SavePlanButton.ActualWidth <= ActualWidth &&
                    point.Y + view.SavePlanButton.ActualHeight <= ActualHeight, "窄窗口大字号保存按钮可滚动到可视区");
                Check(view.CompactNavigation.IsVisible && view.DiagramViewport.ViewportWidth > 150 &&
                    view.DiagramViewport.ViewportHeight > 100 && view.PropertyScroll.ViewportHeight > 100,
                    "窄窗口参数与封装各有独立可用视口");
                var sizeBefore = view.DiagramViewport.ActualHeight;
                Render(this, Path.Combine(output, "narrow-large.png"));
                Check(!Ag32PinMapping.ToolsPanel.IsVisible && Ag32PinMapping.LicenseButton.IsVisible && Ag32PinMapping.OpenButton.IsVisible,
                    "配置 Supra 许可和编辑 pins.ve 固定显示在引脚页顶部");
                await ClickAsync(view.ProjectToolsButton);
                Check(Ag32PinMapping.ToolsPanel.IsVisible && Ag32PinMapping.LicenseButton.IsVisible, "构建状态可展开，顶部操作保持可见");
                await ClickAsync(view.ProjectToolsButton);
                Check(!Ag32PinMapping.ToolsPanel.IsVisible && Ag32PinMapping.LicenseButton.IsVisible, "收起构建状态后许可入口仍可用");
                var priorBottom = BottomRow.ActualHeight;
                await ClickAsync(view.BottomPaneButton);
                UpdateLayout();
                Check(view.DiagramViewport.ActualHeight >= sizeBefore + priorBottom - 2 &&
                    view.HasChanges && Equals(view.UartRx.SelectedValue, 21), "收起底栏把空间交给引脚配置并保留草稿");
                Render(this, Path.Combine(output, "pin-expanded.png"));
                await ClickAsync(view.BottomPaneButton);
                UpdateLayout();
                Check(BottomPanel.IsVisible && ProjectTerminalTab.IsSelected && Math.Abs(priorBottom - BottomRow.ActualHeight) < 1,
                    "展开底栏恢复原终端标签与高度");
                var initialZoom = view.DiagramZoom.Value;
                await ClickAsync(view.ZoomInPackageButton);
                Check(view.DiagramZoom.Value > initialZoom, "通过封装工具栏放大");
                view.DiagramZoom.Value = view.DiagramZoom.Maximum;
                UpdateLayout();
                Check(view.DiagramViewport.ScrollableWidth > 0 && view.DiagramViewport.ScrollableHeight > 0,
                    "封装图放大后可双向平移");
                await ClickAsync(view.FitPackageButton);
                UpdateLayout();
                Check(view.DiagramViewport.ScrollableWidth < 1 && view.DiagramViewport.ScrollableHeight < 1,
                    "适应窗口还原完整封装");
                Render(this, Path.Combine(output, "narrow-fit.png"));
                await ClickAsync(view.UartPreviewButton);
                await Dispatcher.InvokeAsync(() =>
                {
                }, DispatcherPriority.ApplicationIdle);
                Check(view.UartPreview.IsVisible && view.UartPreview.ActualHeight > 0, "可展开并滚动查看实际生成代码");
                Check(Math.Abs(sizeBefore - view.DiagramViewport.ActualHeight) < 1 && view.PropertyScroll.ScrollableHeight > 0,
                    "展开代码只滚动参数区，封装图高度保持不变");
                Render(this, Path.Combine(output, "narrow-code.png"));
                await ClickAsync(view.UartPreviewButton);
            }
            if (template == "minimal")
            {
                var occupied = Path.Combine(root, "src/UART.c");
                await File.WriteAllTextAsync(occupied, "/* application owned */");
                var ve = Path.Combine(root, view.Snapshot!.SourcePath);
                var beforeVe = await File.ReadAllBytesAsync(ve);
                try
                {
                    await services.Ag32PinPlanning.ApplyAsync(root, view.Snapshot, view.GetAssignments(), view.GetClocks(),
                        view.GetAnalog(), view.GetUarts(), CancellationToken.None);
                    throw new InvalidOperationException("应拒绝占用用户 UART.c");
                }
                catch (StudioXException error) when (error.Code == "AG32_SYSTEM_MODIFIED")
                {
                    var afterVe = await File.ReadAllBytesAsync(ve);
                    var afterMain = await File.ReadAllBytesAsync(main);
                    Check((await File.ReadAllTextAsync(occupied)) == "/* application owned */" && beforeVe.SequenceEqual(afterVe)
                        && original.SequenceEqual(afterMain) && !File.Exists(Path.Combine(root, "src/GPIO.c")),
                        "同名用户外设文件保留，冲突不产生半套代码或 VE 改动");
                }
                File.Delete(occupied);
            }
            await ClickAsync(view.SavePlanButton);
            Check(await FindProjectNodeAsync("src/UART.c") is not null && await FindProjectNodeAsync("include/UART.h") is not null &&
                FindEditor("src/main.c")?.Buffer.Text.Contains("SX_UART_Init();") == true,
                "保存后工程树直接显示 UART.c / UART.h，已打开的 main.c 同步显示调用");
            Check(!view.HasChanges && view.Snapshot?.Uarts?.Single().Baud == 115200, "保存并回读串口配置");
            view.UartBaud.Text = "57600";
            await ClickAsync(view.SavePlanButton);
            var regeneratedMain = await File.ReadAllBytesAsync(main);
            var mainText = System.Text.Encoding.UTF8.GetString(regeneratedMain);
            var business = mainText;
            foreach (var module in new[] { "GPIO", "UART" })
            {
                Check(mainText.Split($"SX_{module}_Init();").Length == 2 && mainText.Contains($"#include \"{module}.h\""),
                    $"main 显式且仅一次调用 SX_{module}_Init");
                business = System.Text.RegularExpressions.Regex.Replace(business, $@"(?m)^#include ""{module}\.h""\r?\n|^[ \t]*SX_{module}_Init\(\);\r?\n", "");
            }
            Check(business == System.Text.Encoding.UTF8.GetString(original), "生成加载入口外逐字保留 main 业务代码");
            if (template == "minimal")
            {
                var editor = FindEditor("src/main.c")!;
                editor.Buffer.Text = System.Text.RegularExpressions.Regex.Replace(editor.Buffer.Text,
                    @"(?ms)^[ \t]*/\* STUDIOX GENERATED BEGIN (Includes|Init) \*/.*?^[ \t]*/\* STUDIOX GENERATED END \1 \*/\r?\n", "");
                var headerFile = Path.Combine(root, "include/UART.h");
                File.Delete(headerFile);
                await ClickAsync(view.SavePlanButton);
                var repaired = await File.ReadAllTextAsync(main);
                Check(editor.Buffer.Text == repaired && File.Exists(headerFile) && repaired.Contains("#include \"UART.h\"") &&
                    repaired.Split("SX_UART_Init();").Length == 2 && repaired.Contains("(void)sizeof(int);") &&
                    repaired.IndexOf("/* USER CODE END BeforeInit */", StringComparison.Ordinal) < repaired.IndexOf("SX_UART_Init();", StringComparison.Ordinal) &&
                    repaired.IndexOf("SX_UART_Init();", StringComparison.Ordinal) < repaired.IndexOf("/* USER CODE BEGIN AfterInit */", StringComparison.Ordinal),
                    "在编辑器删除两个生成区和 UART.h 后，原配置直接保存可补回代码并保留用户区");
            }
            var uartCode = await File.ReadAllTextAsync(Path.Combine(root, "src/UART.c"));
            Check(uartCode.Contains("void SX_UART_Init(void)") && uartCode.Contains("UART_Init(UART0") &&
                uartCode.Contains("UART_EnableRTS(UART0)") && uartCode.Contains("UART0_isr"), "UART.c 包含实际初始化、流控和接收中断");
            Check(!(await File.ReadAllTextAsync(Path.Combine(root, Ag32SystemSupport.SourcePath))).Contains("UART_Init("),
                "系统启动层不再重复初始化 UART");
            var generated = await File.ReadAllTextAsync(Path.Combine(root, "src/GPIO.c"));
            var high = generated.IndexOf("GPIO_SetHigh(GPIO4, GPIO_BIT4)", StringComparison.Ordinal);
            Check(high >= 0 && generated.IndexOf("GPIO_SetOutput(GPIO4, GPIO_BIT4)", StringComparison.Ordinal) > high &&
                generated.Contains("GPIO_INTMODE_BOTHEDGE") && generated.Contains("++StudioX_GPIO4_5_InterruptCount"), "先预置输出高再使能输出，GPIO 中断生成对应处理程序");
            await CloseProjectAsync(CancellationToken.None);
            await OpenProjectAsync(root, CancellationToken.None);
            await ClickAsync(Ag32PinMappingRailButton);
            view.UartPort.SelectedItem = "UART0";
            Check(view.UartBaud.Text == "57600" && view.UartIrq.IsChecked == true, "关闭重开还原参数");
            Check(Equals(view.UartMode.SelectedValue, "TX_RX") && Equals(view.UartParity.SelectedValue, "EVEN") && Equals(view.UartRts.SelectedValue, 12) &&
                Equals(view.UartCts.SelectedValue, 13) && Equals(view.UartPriority.SelectedItem, 4) &&
                view.GetAssignments().Any(pin => pin.InitialHigh && pin.SlewControl), "关闭重开还原帧格式、流控、电气配置与中断优先级");
            if (layoutOnly)
            {
                view.UartEnabled.IsChecked = false;
                await ClickAsync(view.SavePlanButton);
                var source = await File.ReadAllTextAsync(Path.Combine(root, "src/UART.c"));
                var disabledMain = await File.ReadAllTextAsync(main);
                Check(!source.Contains("StudioX_UART0_TryRead") && view.GetAssignments().Length == 6 &&
                    !disabledMain.Contains("SX_UART_Init();") && !disabledMain.Contains("#include \"UART.h\"") &&
                    disabledMain.Contains("SX_GPIO_Init();") && disabledMain.Contains("(void)sizeof(int);"),
                    "关闭 UART 后仅撤下对应生成内容，保留 GPIO、引脚与用户代码");
            }
            await CheckBusConfigurationAsync();
            if (layoutOnly)
            {
                await CloseProjectAsync(CancellationToken.None);
                return;
            }
            async Task CheckBusConfigurationAsync()
            {
                void Pick(string function)
                {
                    var combo = view.BusPinRows.Children.OfType<DockPanel>().SelectMany(row => row.Children.OfType<ComboBox>())
                        .Single(box => Equals(box.Tag, function));
                    combo.SelectedIndex = 1;
                    Check(combo.SelectedValue is int n && n > 0 && view.GetAssignments().Any(p => p.Function == function && p.PinNumber == n),
                        function + " 使用可分配的封装脚");
                }
                var spi = template == "minimal" ? "SPI1" : "SPI0";
                if (spi == "SPI0")
                {
                    // SPI0_SCK 和前一项回归用的 GPIO4_5 共享一个硬件资源。
                    var pad = view.PackageDiagram.Children.OfType<Button>().Single(button => Equals(button.Tag, 11));
                    await ClickAsync(pad);
                    var reset = view.ActivePinMenu!.ResetItem;
                    ((IInvokeProvider)new MenuItemAutomationPeer(reset).GetPattern(PatternInterface.Invoke)).Invoke();
                    await Dispatcher.InvokeAsync(() =>
                    {
                    }, DispatcherPriority.ApplicationIdle);
                }
                view.UartPort.SelectedItem = spi;
                view.BusEnabled.IsChecked = true;
                Check(!view.SavePlanButton.IsEnabled && view.BusStatus.Text.Contains("SCK"), "SPI 缺少时钟脚时就地提示");
                Pick(spi + "_SCK");
                Pick(spi + "_SI_IO0");
                view.BusRate.SelectedItem = 32;
                view.SpiBigEndian.IsChecked = true;
                Check(view.BusCode.Text.Contains("SPI_CTRL_SCLK_DIV32") && view.BusCode.Text.Contains("SPI_CTRL_ENDIAN"),
                    "分频与字节序进入 SPI 初始化");
                foreach (var module in new[] { "I2C0", "I2C1" })
                {
                    view.UartPort.SelectedItem = module;
                    view.BusEnabled.IsChecked = true;
                    Pick(module + "_SCL");
                    Pick(module + "_SDA");
                    view.BusRate.SelectedItem = module == "I2C0" ? 400000 : 100000;
                    Check(view.BusCode.Text.Contains("I2C_Enable(" + module + ")") && view.BusTiming.Text.Contains("外部上拉"), module + " 显示实际 SCL 与上拉提示");
                }
                view.UartPort.SelectedItem = "CAN0";
                view.BusEnabled.IsChecked = true;
                Pick("CAN0_RX0");
                Pick("CAN0_TX0");
                view.CanSample.SelectedValue = 800;
                Check(view.BusCode.Text.Contains("CAN_Init(CAN0") && view.BusTiming.Text.Contains("实际采样点"), "CAN 根据时钟求解时序");
                try
                {
                    Ag32PinPlanningService.PreviewBus(new("CAN0", BitRate: 333333, SamplePointPermille: 875),
                        view.GetAssignments(), new(null, null, null), out _);
                    throw new InvalidOperationException("不可能的 CAN 位率通过了校验");
                }
                catch (StudioXException ex) when (ex.Code == "AG32_BUS_INIT")
                {
                    Check(ex.Message.Contains("333333"), "CAN 不能产生目标位率时解释原因");
                }
                view.BusCodePanel.IsExpanded = true;
                if (template == "minimal")
                {
                    UpdateLayout();
                    Render(this, Path.Combine(output, "can-pin-planner.png"));
                }
                await ClickAsync(view.SavePlanButton);
                Check(!view.HasChanges && view.Snapshot!.Buses.Length == 4, "一次保存四个总线实例");
                var stored = await services.Ag32PinPlanning.ReadAsync(root);
                Check(stored.Buses.SequenceEqual(view.GetBuses()) && stored.Buses.Single(s => s.Module == "I2C0").BitRate == 400000 &&
                    stored.Buses.Single(s => s.Module == spi).BigEndian, "重读 VE 恢复各实例独立的时基和字节序");
                var mainSource = await File.ReadAllTextAsync(main);
                foreach (var type in new[] { "SPI", "I2C", "CAN" })
                {
                    Check(mainSource.Split("SX_" + type + "_Init();").Length == 2 && File.Exists(Path.Combine(root, "include", type + ".h")) &&
                        await FindProjectNodeAsync("src/" + type + ".c") is not null, type + " 在 src 生成源码并在 main 中初始化一次");
                }
                var disabled = template == "minimal" ? "I2C1" : "I2C0";
                var retained = disabled == "I2C1" ? "I2C0" : "I2C1";
                view.UartPort.SelectedItem = disabled;
                view.BusEnabled.IsChecked = false;
                await ClickAsync(view.SavePlanButton);
                var i2c = await File.ReadAllTextAsync(Path.Combine(root, "src/I2C.c"));
                Check(i2c.Contains($"I2C_Enable({retained})") && !i2c.Contains($"I2C_Enable({disabled})") &&
                    view.GetAssignments().Any(pin => pin.Function == disabled + "_SCL"), "关闭单个 I²C 实例保留引脚和同组另一路初始化");
            }
            Build_Click(this, new RoutedEventArgs());
            await pendingOperation;
            await File.WriteAllTextAsync(Path.Combine(output, template + "-build.log"), BuildLog.Text);
            Check(File.Exists(Path.Combine(root, ".build", "firmware.elf")) && File.Exists(Path.Combine(root, ".build", "ag32-mapping", "pins.bin")) &&
                Status.Text.Contains("编译成功", StringComparison.Ordinal), template + " 正常构建入口生成固件和映射镜像");
            await CloseProjectAsync(CancellationToken.None);
        }
    }
}
