namespace StudioX.Desktop;

using System.Security.Cryptography;
using System.Text.Json;
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
    /// <summary>隔离副本验证图形转换、编辑同步与审批卡；不启动 Supra 或硬件进程。</summary>
    public async Task RenderAg32MappingPreviewAsync(string directory, string sourceProject)
    {
        var sourceManifest = await ProjectService.ReadAsync(sourceProject);
        if (sourceManifest.DeviceId != "AG32VF303CCT6")
        {
            throw new ArgumentException("UI 验证需要 AG32VF303CCT6 工程。");
        }
        var fixture = Path.Combine(directory, "fixture");
        if (Directory.Exists(fixture))
        {
            throw new InvalidOperationException("请选择新的 UI 验证输出目录。");
        }
        var originalVe = await File.ReadAllBytesAsync(PathBoundary.Resolve(sourceProject, "logic/pins.ve"));
        foreach (var source in Directory.EnumerateFiles(sourceProject, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceProject, source).Replace('\\', '/');
            if (relative.StartsWith(".build/", StringComparison.Ordinal) || relative.StartsWith(".git/", StringComparison.Ordinal) ||
                relative.StartsWith("logic/.pio/", StringComparison.Ordinal) || relative.StartsWith(".pio/", StringComparison.Ordinal))
            {
                continue;
            }
            var target = PathBoundary.Resolve(fixture, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(PathBoundary.Resolve(sourceProject, relative), target);
        }
        await JsonStore.WriteAsync(PathBoundary.Resolve(fixture, ".studiox/project.json"), sourceManifest with
        {
            PinMapping = null,
            Logic = null
        });
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new InvalidOperationException(name);
            }
            checks.Add(name);
        }
        async Task LayoutAsync()
        {
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        }
        async Task<Ag32DownloadConfirmationWindow> WaitForDownloadDialogAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Ag32PinMapping.ActiveDownloadConfirmation is not { IsVisible: true })
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new InvalidOperationException("下载确认弹窗没有打开。");
                }
                await Task.Delay(20);
            }
            await LayoutAsync();
            return Ag32PinMapping.ActiveDownloadConfirmation!;
        }
        await OpenProjectAsync(fixture, CancellationToken.None);
        try
        {
            Check(DownloadProbePicker.Items.Count == 2 &&
                DownloadProbePicker.Items.Cast<StudioX.Packages.DebugProbeDefinition>().Select(probe => probe.Id)
                    .SequenceEqual(new[] { "cmsis-dap", "jlink" }), "旧 AG32 工程显示 DAP 与 J-Link 两种烧录器");
            DownloadProbePicker.SelectedValue = "jlink";
            await pendingOperation;
            Check(downloadConfiguration?.Options.ProbeId == "jlink" &&
                ((StudioX.Packages.DebugProbeDefinition)DownloadProbePicker.SelectedItem).DisplayName.Contains("V9", StringComparison.Ordinal),
                "J-Link 标注 V9 及以上并保存工程选择");
            DownloadProbePicker.SelectedValue = "cmsis-dap";
            await pendingOperation;
            Ag32PinMappingRailButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pendingOperation;
            Check(Ag32PinMappingRailButton.Visibility == Visibility.Visible && Ag32PinMappingTab.IsSelected &&
                Ag32PinMapping.EnableButton.Visibility == Visibility.Visible,
                "左侧 AG32 引脚分配入口打开映射页，旧工程显示显式启用入口");
            EnableAg32PinMapping_Click(this, EventArgs.Empty);
            await pendingOperation;
            Check(currentProjectManifest?.PinMapping is not null && CodeLanguage.ForFile(activeDocument?.RelativePath ?? "") == "AGM Pin Map",
                "启用更新工程元数据并打开 VE 编辑器");
            Check(File.ReadAllBytes(PathBoundary.Resolve(fixture, "logic/pins.ve")).SequenceEqual(originalVe),
                "已有 VE 的时钟、映射和编码原字节保留");
            Check(Ag32PinMapping.EnableButton.Visibility == Visibility.Collapsed && Ag32PinMapping.OpenButton.IsEnabled &&
                Ag32PinMapping.StatusText.Text.Contains("Supra 许可", StringComparison.Ordinal),
                "启用后去掉引导并显示本机许可与凭据状态");
            // 图形保存只操作夹具；故意采用带 BOM 和 CRLF 的 VE，验证实时编辑器同步。
            ClearEditorDocuments();
            var graphicalText = "# 图形化验收原注释\r\nGPIO4_4 PIN_21 # LED\r\nHSECLK 8\r\nSYSCLK 200\r\nBUSCLK 100\r\n";
            await File.WriteAllTextAsync(PathBoundary.Resolve(fixture, "logic/pins.ve"), graphicalText, new System.Text.UTF8Encoding(true));
            await OpenSourceAsync("logic/pins.ve", CancellationToken.None);
            await ShowAg32PinMappingAsync(CancellationToken.None);
            var planner = Ag32PinMapping.Planner;
            Check(planner.Visibility == Visibility.Visible && planner.Snapshot is { CanEdit: true, Pins.Length: 48 },
                "AG32 映射页加载真实 48 脚图形与厂商功能目录");
            Ag32PinFunctionMenu OpenPinMenu(int number)
            {
                planner.PackageDiagram.Children.OfType<Button>().Single(button => Equals(button.Tag, number))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return planner.ActivePinMenu ?? throw new InvalidOperationException("点击引脚没有打开功能菜单。");
            }
            static MenuItem FunctionItem(Ag32PinFunctionMenu menu, string name) => menu.Items.OfType<MenuItem>()
                .Single(item => item.Tag is Ag32PinFunction function && function.Name == name);
            async Task ChooseAsync(MenuItem item)
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                // Invoke 经过 WPF 的关闭、延迟 Click 流程；直接 RaiseEvent 会遗漏该顺序。
                ((IInvokeProvider)new MenuItemAutomationPeer(item).GetPattern(PatternInterface.Invoke)).Invoke();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }

            var pinMenu = OpenPinMenu(2);
            Check(pinMenu.IsOpen && !planner.HasChanges && planner.GetAssignments().Single().PinNumber == 21,
                "左键点击打开引脚菜单，未选功能时不改变分配");
            pinMenu.IsOpen = false;
            // WPF 在弹出层完成关闭后发出 Closed，等待输入队列清理引用。
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(planner.ActivePinMenu is null && !planner.HasChanges, "关闭菜单不创建脏草稿");
            pinMenu = OpenPinMenu(2);
            pinMenu.SearchBox.Text = "gpio4_4";
            Check(pinMenu.Items.OfType<MenuItem>().Where(item => item.Tag is Ag32PinFunction && item.Visibility == Visibility.Visible)
                .Select(item => ((Ag32PinFunction)item.Tag).Name).SequenceEqual(new[] { "GPIO4_4" }),
                "搜索不区分大小写，仅显示匹配功能");
            var gpioItem = FunctionItem(pinMenu, "GPIO4_4");
            Check(((StackPanel)gpioItem.Header).Children.OfType<TextBlock>().First().Text == "GPIO4_4" &&
                ((StackPanel)gpioItem.Header).Children.OfType<TextBlock>().Last().Text.Contains("PIN_21", StringComparison.Ordinal),
                "菜单保留功能下划线并提示占用的引脚");
            await ChooseAsync(gpioItem);
            Check(!pinMenu.IsOpen && planner.ActivePinMenu is null, "选择功能后立即关闭菜单");
            Check(planner.HasChanges && planner.GetAssignments().Length == 2 &&
                planner.AssignmentsGrid.SelectedItem is Ag32PinAssignment { PinNumber: 2 },
                "同一功能分配到新引脚时保留原分配供冲突修正");
            Check(!planner.SavePlanButton.IsEnabled && planner.ConflictStatus.Visibility == Visibility.Visible &&
                planner.ConflictStatus.Text.Contains("PIN_21", StringComparison.Ordinal) &&
                planner.ConflictStatus.Text.Contains("PIN_2", StringComparison.Ordinal),
                "冲突列出两个物理引脚并禁止保存");
            foreach (var number in new[] { 2, 21 })
            {
                var button = planner.PackageDiagram.Children.OfType<Button>().Single(button => Equals(button.Tag, number));
                var lead = ((Canvas)button.Content).Children.OfType<Border>().Single(border => border.Child is TextBlock);
                Check(lead.Background.ToString() == FindResource("ErrorColor").ToString() &&
                    ((TextBlock)lead.Child).Text == "GPIO4_4", $"PIN_{number} 整条标红且直接显示功能名");
            }
            await LayoutAsync();
            Render(planner.PackageDiagram, Path.Combine(directory, "pin-conflict.png"));
            Check((await services.Files.ReadAsync(fixture, "logic/pins.ve")).Text.Contains("PIN_21", StringComparison.Ordinal),
                "未保存图形草稿不会改写 VE");
            await RefreshAg32PinMappingStatusAsync(CancellationToken.None);
            Check(planner.HasChanges && planner.GetAssignments().Length == 2,
                "后台状态刷新保留图形草稿");
            await ChooseAsync(OpenPinMenu(21).ResetItem);
            Check(planner.SavePlanButton.IsEnabled && planner.ConflictStatus.Visibility == Visibility.Collapsed &&
                planner.GetAssignments().Single().PinNumber == 2, "移除旧分配后消除冲突并恢复保存");
            planner.SavePlanButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pendingOperation;
            Check(!planner.HasChanges && planner.Snapshot!.Assignments.Single().PinNumber == 2 &&
                activeEditor!.Buffer.Text.Contains("GPIO4_4 PIN_2", StringComparison.Ordinal) && !activeEditor.IsDirty,
                "图形保存生成实际约束并立即同步已打开的 VE 编辑器");
            Check(planner.SdcButton.IsEnabled && planner.VexButton.IsEnabled &&
                planner.ConstraintDetails.Text.Contains("studiox-clocks.sdc", StringComparison.Ordinal),
                "保存后可查看厂商引脚约束与时钟 SDC");
            Check((await File.ReadAllBytesAsync(PathBoundary.Resolve(fixture, "logic/pins.ve"))) is [0xef, 0xbb, 0xbf, ..] &&
                activeEditor!.Buffer.Text.Contains("# 图形化验收原注释", StringComparison.Ordinal),
                "图形保存保留 BOM、中文原注释");
            planner.SdcButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await pendingOperation;
            Check(activeDocument?.RelativePath.EndsWith("studiox-clocks.sdc", StringComparison.Ordinal) == true &&
                activeEditor!.Buffer.Text.Contains("create_clock", StringComparison.Ordinal),
                "约束按钮打开实际生成的 SDC 文档");
            ShowDocument(Ag32PinMappingTab);
            await LayoutAsync();
            await ChooseAsync(FunctionItem(OpenPinMenu(21), "GPIO4_4"));
            Check(HasUnsavedAiProjectChanges(), "图形草稿进入 Agent 未保存修改保护");
            await ChooseAsync(OpenPinMenu(2).ResetItem);
            await SaveAllSourcesAsync(fixture, CancellationToken.None);
            Check(!planner.HasChanges && planner.Snapshot!.Assignments.Single().PinNumber == 21,
                "保存全部先提交图形草稿，普通编译不会遗漏映射修改");
            var originalSnapshot = planner.Snapshot!;
            await ChooseAsync(FunctionItem(OpenPinMenu(21), "GPIO4_4"));
            Check(!planner.HasChanges, "重复选择当前功能不产生脏草稿");
            var replacement = originalSnapshot.Functions.First(function => function.Name.StartsWith("GPIO", StringComparison.Ordinal) && function.Name != "GPIO4_4");
            await ChooseAsync(FunctionItem(OpenPinMenu(21), replacement.Name));
            Check(planner.GetAssignments() is [{ PinNumber: 21 } replaced] && replaced.Function == replacement.Name,
                "菜单选择其它功能仅替换当前物理引脚");
            planner.SetSnapshot(originalSnapshot);
            var reservedMenu = OpenPinMenu(originalSnapshot.Pins.First(pin => !pin.CanAssign).Number);
            Check(!reservedMenu.Items.OfType<MenuItem>().Any(item => item.Tag is Ag32PinFunction) && !planner.HasChanges,
                "保留引脚仅显示说明，不提供可分配功能");
            var staleMenu = OpenPinMenu(2);
            planner.SetBusy(true);
            FunctionItem(staleMenu, "GPIO4_4").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(!staleMenu.IsOpen && planner.ActivePinMenu is null && !planner.HasChanges,
                "进入忙碌状态关闭菜单并拒绝旧菜单选择");
            planner.SetBusy(false);
            staleMenu = OpenPinMenu(2);
            planner.SetSnapshot(originalSnapshot);
            FunctionItem(staleMenu, "GPIO4_4").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(!staleMenu.IsOpen && !planner.HasChanges && planner.GetAssignments().Single().PinNumber == 21,
                "重载引脚数据使旧菜单失效");
            planner.SetSnapshot(originalSnapshot with
            {
                CanEdit = false
            });
            var readOnlyMenu = OpenPinMenu(2);
            Check(!readOnlyMenu.Items.OfType<MenuItem>().Any(item => item.Tag is Ag32PinFunction) && !planner.HasChanges,
                "只读 VE 的引脚菜单不提供分配功能");
            planner.SetSnapshot(originalSnapshot);
            await RefreshPacksAsync(CancellationToken.None);
            ShowDocument(Ag32PinMappingTab);
            foreach (var profile in Ag32DeviceCatalog.All)
            {
                // 只创建读目录所需的小夹具；封装与固定脚来自真实包和锁定转换器。
                var pack = installedPacks.Single(item => item.Manifest.Devices.Any(device => device.Id == profile.DeviceId));
                var device = pack.Manifest.Devices.Single(item => item.Id == profile.DeviceId);
                var modelRoot = Path.Combine(directory, "diagram-models", profile.DeviceId);
                var modelProject = ProjectService.Plan(pack, profile.DeviceId, device.Templates.First().Id, "diagram_fixture").Project;
                await JsonStore.WriteAsync(PathBoundary.Resolve(modelRoot, ".studiox/project.json"), modelProject);
                await JsonStore.WriteAsync(PathBoundary.Resolve(modelRoot, "device/manifest.json"), pack.Manifest);
                Directory.CreateDirectory(Path.Combine(modelRoot, "logic"));
                await File.WriteAllTextAsync(Path.Combine(modelRoot, "logic/pins.ve"), "GPIO4_4 PIN_2\n");
                var modelSnapshot = await services.Ag32PinPlanning.ReadAsync(modelRoot);
                // 独立图形不参加当前工程的激活刷新，避免截图时被 CCT6 页面的状态回填。
                var modelDiagram = new Ag32PackageDiagram();
                modelDiagram.SetPackage(profile.DeviceId, profile.PackageName);
                modelDiagram.SetPins(modelSnapshot.Pins.Select(pin => new Ag32PackagePinVisual(pin.Number,
                    pin.CanAssign, pin.Number == 2 ? "GPIO4_4" : null)).ToArray(), 2);
                modelDiagram.Measure(new Size(modelDiagram.Width, modelDiagram.Height));
                modelDiagram.Arrange(new Rect(0, 0, modelDiagram.Width, modelDiagram.Height));
                modelDiagram.UpdateLayout();
                Check(modelDiagram.Children.OfType<Button>().Count() == profile.PinCount,
                    "四边封装图完整呈现 " + profile.DeviceId + " / " + profile.PackageName);
                Render(modelDiagram, Path.Combine(directory, "package-" + profile.DeviceId + ".png"));
            }
            planner.SetSnapshot(originalSnapshot);
            planner.AssignmentsGrid.SelectedItem = planner.GetAssignments().Single();
            var initialDiagramZoom = planner.DiagramZoom.Value;
            planner.DiagramZoom.Value = 1200;
            await LayoutAsync();
            var diagramViewport = (ScrollViewer)((Viewbox)planner.PackageDiagram.Parent).Parent;
            Check(diagramViewport.ScrollableHeight > 0 && diagramViewport.ScrollableWidth > 0,
                "高脚数封装可放大并在两轴滚动查看");
            planner.DiagramZoom.Value = initialDiagramZoom;
            if (BottomPanel.Visibility == Visibility.Visible)
            {
                ToggleBottom_Click(this, new RoutedEventArgs());
            }
            // 将图形区滚动到可视区域，截图检查完整封装与操作表格。
            ((ScrollViewer)Ag32PinMapping.Content).ScrollToVerticalOffset(250);
            await LayoutAsync();
            Render(this, Path.Combine(directory, "graphical-pin-planning.png"));
            ClearEditorDocuments();
            await File.WriteAllBytesAsync(PathBoundary.Resolve(fixture, "logic/pins.ve"), originalVe);
            await OpenSourceAsync("logic/pins.ve", CancellationToken.None);
            await ShowAg32PinMappingAsync(CancellationToken.None);
            var veEditor = activeEditor ?? throw new InvalidOperationException("缺少 VE 文档。");
            var disk = await services.Files.ReadAsync(fixture, "logic/pins.ve");
            await File.WriteAllTextAsync(PathBoundary.Resolve(fixture, "logic/pins.ve"), disk.Text + "# MCP 外部写入验证\n");
            await SyncAiEditorAfterWriteAsync(fixture, aiProjectGeneration, "logic/pins.ve", false);
            Check(veEditor.Buffer.Text.Contains("MCP 外部写入验证", StringComparison.Ordinal) && !veEditor.IsDirty,
                "已保存 VE 的工具写入实时同步编辑区");
            veEditor.Buffer.Insert(0, "# 用户未保存内容\n");
            await File.WriteAllTextAsync(PathBoundary.Resolve(fixture, "logic/pins.ve"), disk.Text + "# 第二份磁盘内容\n");
            await SyncAiEditorAfterWriteAsync(fixture, aiProjectGeneration, "logic/pins.ve", false);
            Check(veEditor.IsDirty && veEditor.Buffer.Text.Contains("用户未保存内容", StringComparison.Ordinal) &&
                !veEditor.Buffer.Text.Contains("第二份磁盘内容", StringComparison.Ordinal), "VE 脏缓冲区保留，不被工具写入覆盖");
            // 脏缓冲区验证已经完成；只在夹具丢弃两份测试修改，不能绕过正常保存的磁盘哈希检查。
            ClearEditorDocuments();
            await File.WriteAllTextAsync(PathBoundary.Resolve(fixture, "logic/pins.ve"), disk.Text);
            await OpenSourceAsync("logic/pins.ve", CancellationToken.None);
            File.Delete(PathBoundary.Resolve(fixture, "logic/pins.ve"));
            await ShowAg32PinMappingAsync(CancellationToken.None);
            Check(Ag32PinMapping.EnableButton.Visibility == Visibility.Visible &&
                Ag32PinMapping.EnableButton.Content is string repairText && repairText == "恢复 .ve 模板",
                "已启用工程的缺失 VE 显示显式恢复入口");
            EnableAg32PinMapping_Click(this, EventArgs.Empty);
            await pendingOperation;
            Check(File.Exists(PathBoundary.Resolve(fixture, "logic/pins.ve")) && activeEditor?.Buffer.Text ==
                File.ReadAllText(PathBoundary.Resolve(fixture, "logic/pins.ve")),
                "恢复模板同步已打开的干净 VE 缓冲区");
            ClearEditorDocuments();
            await File.WriteAllTextAsync(PathBoundary.Resolve(fixture, "logic/pins.ve"), disk.Text);
            await OpenSourceAsync("logic/pins.ve", CancellationToken.None);
            await ShowAg32PinMappingAsync(CancellationToken.None);
            Width = 1380;
            Height = 980;
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
            {
                ApplyTheme(theme);
                await LayoutAsync();
                Render(this, Path.Combine(directory, "mapping-" + theme.Id + ".png"));
                var themedMenu = OpenPinMenu(2);
                await LayoutAsync();
                themedMenu.UpdateLayout();
                Check(themedMenu.IsVisible && themedMenu.ActualHeight <= 460 && themedMenu.ActualWidth >= 285,
                    "引脚菜单限制高度并可滚动 " + theme.Id);
                Render(themedMenu, Path.Combine(directory, "pin-menu-" + theme.Id + ".png"));
                themedMenu.SearchBox.Text = "GPIO4_4";
                await LayoutAsync();
                themedMenu.UpdateLayout();
                Render(themedMenu, Path.Combine(directory, "pin-menu-filtered-" + theme.Id + ".png"));
                themedMenu.IsOpen = false;
                var details = "离线审批显示验收，不执行下载。\n\napplication：.build/firmware.bin\n地址：0x80000000 · 4096 字节\nSHA-256：" + new string('A', 64)
                    + "\n\npin-mapping：.build/ag32-pin-mapping/pins.bin\n地址：0x80027000 · 102400 字节\nSHA-256：" + new string('B', 64);
                var pending = Ag32PinMapping.ConfirmDownloadAsync(details, () => true, CancellationToken.None);
                var dialog = await WaitForDownloadDialogAsync();
                Check(dialog.Owner == this && dialog.DetailsText.Text == details, "双镜像弹窗完整显示并关联主窗口 " + theme.Id);
                Render(dialog, Path.Combine(directory, "mapping-approval-" + theme.Id + ".png"));
                dialog.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!await pending && Ag32PinMapping.ActiveDownloadConfirmation is null && !dialog.IsVisible,
                    "取消后关闭弹窗并清理引用 " + theme.Id);
            }
            // 从源码标签下载同样应弹窗，不依赖映射页是否选中。
            ShowDocument(activeEditor!.Tab);
            var previousTab = WorkspaceTabs.SelectedItem;
            var approved = Ag32PinMapping.ConfirmDownloadAsync("离线批准验收，不执行工具。", () => true, CancellationToken.None);
            var approveDialog = await WaitForDownloadDialogAsync();
            Check(approveDialog.Owner == this && WorkspaceTabs.SelectedItem == previousTab, "源码页直接弹窗且保留当前标签");
            approveDialog.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(await approved && Ag32PinMapping.ActiveDownloadConfirmation is null, "批准后关闭弹窗");
            var dismissed = Ag32PinMapping.ConfirmDownloadAsync("关闭窗口验收。", () => true, CancellationToken.None);
            (await WaitForDownloadDialogAsync()).Close();
            Check(!await dismissed && Ag32PinMapping.ActiveDownloadConfirmation is null, "窗口关闭按取消处理");
            using var cancellation = new CancellationTokenSource();
            var cancelled = Ag32PinMapping.ConfirmDownloadAsync("取消等待验收。", () => true, cancellation.Token);
            await WaitForDownloadDialogAsync();
            cancellation.Cancel();
            Check(!await cancelled && Ag32PinMapping.ActiveDownloadConfirmation is null, "停止等待关闭弹窗");
            var isCurrent = true;
            var stale = Ag32PinMapping.ConfirmDownloadAsync("工程切换验收。", () => isCurrent, CancellationToken.None);
            await WaitForDownloadDialogAsync();
            isCurrent = false;
            Check(!await stale && Ag32PinMapping.ActiveDownloadConfirmation is null, "工程失效关闭弹窗并拒绝旧请求");
            await CloseProjectAsync(CancellationToken.None);
            Check(Ag32PinMappingTab.Visibility == Visibility.Collapsed && Ag32PinMappingRailButton.Visibility == Visibility.Collapsed,
                "关闭工程清理映射入口");
            Check(File.ReadAllBytes(PathBoundary.Resolve(sourceProject, "logic/pins.ve")).SequenceEqual(originalVe), "源工程 VE 不变");
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
            {
                success = true,
                checks,
                hardwareConnected = false,
                vendorConverterExecuted = true,
                supraExecuted = false,
                sourceVeSha256 = Convert.ToHexString(SHA256.HashData(originalVe))
            }, JsonStore.Options));
        }
        finally
        {
            // 失败夹具也丢弃测试缓冲区，自动退出不进入用户文件保存对话框。
            ClearEditorDocuments();
        }
    }
}
