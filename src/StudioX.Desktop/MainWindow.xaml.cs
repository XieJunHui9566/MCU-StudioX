namespace StudioX.Desktop;

using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using StudioX.Application;
using StudioX.Devices;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

public partial class MainWindow : Window
{
    private readonly WorkbenchService services;
    private InstalledPack[] installedPacks = [];
    private bool bundledPacksChecked;
    private string? projectDirectory;
    private ThemeDefinition currentTheme = ThemeService.Dark;
    private CancellationTokenSource? operationCancellation;
    private Task pendingOperation = Task.CompletedTask;
    private DeviceSession? simulation;
    private CancellationTokenSource? simulationCancellation;
    private FrameSubscription? logSubscription;
    private FrameSubscription? plotSubscription;
    private Task[] consumers = [];
    private CancellationTokenSource? captureCancellation;
    private FrameSubscription? captureSubscription;
    private Task? captureTask;
    private bool closing;
    private bool closed;
    private long logFrames;
    private long plotFrames;

    public MainWindow(WorkbenchService services)
    {
        this.services = services;
        InitializeComponent();
        InitializeBuildSettings();
        InitializeStcIsp();
        SerialView.Attach(services.Serial);
        SerialPlotView.Attach(services.SerialPlot);
        ProjectTerminal.Attach(services.Terminal);
        GitGraph.Attach(services.GitGraph);
        GitGraph.LogDiagnostic = Log;
        GitGraph.ExecuteOperationAsync = RunAsync;
        GitGraph.MutationStateChanged = active => CancelButton.IsEnabled = !active && operationCancellation is not null;
        GitGraph.BeforeStageAsync = PrepareGitStageAsync;
        GitGraph.BeforeWorkingTreeChangeAsync = PrepareGitWorkingTreeChangeAsync;
        GitGraph.WorkingTreeChangedAsync = ApplyGitWorkingTreeChangeAsync;
        GitHubWorkspace.Attach(services.GitHubAccounts, services.GitGraph, services.GitHubPullRequests);
        GitHubWorkspace.SelectedAccountChanged += GitHubSelectedAccountChanged;
        GitHubWorkspace.RepositoryValidated += GitHubRepositoryValidated;
        GitHubWorkspace.LogDiagnostic = Log;
        GitHubWorkspace.BeforeWorkingTreeChangeAsync = PrepareGitHubWorkingTreeChangeAsync;
        GitHubWorkspace.WorkingTreeChangedAsync = ApplyGitHubWorkingTreeChangeAsync;
        GitHubWorkspace.OpenClonedRepositoryAsync = OpenClonedGitHubRepositoryAsync;
        InitializeEditor();
    }

    public async Task InitializeAsync()
    {
        await RunAsync(async token =>
        {
            // 首页记录与器件仓库无关，优先显示；不扫描工程目录或校验所有 SDK。
            try { await RefreshRecentAsync(token); }
            catch (Exception ex) { Log("最近工程读取失败：" + ex.Message); }
            try { ApplyTheme(await services.Themes.LoadAsync(token)); }
            catch (Exception ex) { Log("主题恢复失败，使用默认主题：" + ex.Message); ApplyTheme(ThemeService.Dark); }
            try { ApplyEditorSettings(await services.EditorSettings.LoadAsync(token)); }
            catch (Exception ex) { Log("编辑器设置恢复失败：" + ex.Message); ApplyEditorSettings(new()); }
            try { ApplyBackground(await services.Appearance.LoadAsync(token)); }
            catch (Exception ex) { Log("背景恢复失败：" + ex.Message); Status.Text = "背景不可用，可在外观设置中重新选择。"; }
            ToolInventory.Text = await services.ToolInventory.DescribeAsync(verify: false, token: token);
            PluginPicker.ItemsSource = services.PluginManifests.ToArray();
            try { await GitHubWorkspace.RefreshAccountsForChromeAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { Log("GitHub 账号状态读取失败：" + ex.Message); }
        });
    }

    public void ApplyTheme(ThemeDefinition theme)
    {
        ThemeService.Validate(theme);
        foreach (var (key, value) in theme.Colors)
            System.Windows.Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        System.Windows.Application.Current.Resources[SystemColors.ControlBrushKey] = System.Windows.Application.Current.Resources["Surface"];
        currentTheme = theme;
        RefreshSurfaceBrushes();
        ApplyEditorTheme();
        Plot.InvalidateVisual();
        SerialPlotView.RefreshTheme();
    }
    private async void ToggleTheme_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        var theme = currentTheme.Id == ThemeService.Dark.Id ? ThemeService.Light : ThemeService.Dark;
        await services.Themes.SelectAsync(theme, token); ApplyTheme(theme); Status.Text = theme.DisplayName;
    });
    private async void ImportTheme_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "StudioX JSON 主题|*.json" };
        if (dialog.ShowDialog(this) == true) await RunAsync(async token => ApplyTheme(await services.Themes.ImportAsync(dialog.FileName, token)));
    }

    private async Task RefreshPacksAsync(CancellationToken token, bool preserveSelection = false)
    {
        if (!bundledPacksChecked)
        {
            try
            {
                var bundled = Path.Combine(AppContext.BaseDirectory, "device-packs");
                var result = await services.Packs.ImportBundledMissingAsync(bundled, token);
                bundledPacksChecked = true;
                if (result.Imported > 0) Log($"已导入 {result.Imported} 个随附器件包。");
                foreach (var failure in result.Failures)
                    Log($"随附器件包导入失败：{failure.File}：{failure.Message}");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                bundledPacksChecked = true;
                Log("读取随附器件包失败，可手动导入：" + ex.Message);
            }
        }
        var catalog = await services.Packs.ListCatalogAsync(token);
        // 目录读取期间允许用户继续选择；在实际重建列表前采集最新选择。
        var vendorId = preserveSelection ? (VendorPicker.SelectedItem as ManufacturerOption)?.Id : null;
        var packId = preserveSelection ? (PackPicker.SelectedItem as InstalledPack)?.Manifest.Id : null;
        var packVersion = preserveSelection ? (PackPicker.SelectedItem as InstalledPack)?.Manifest.Version : null;
        var deviceId = preserveSelection ? (DevicePicker.SelectedItem as DeviceDefinition)?.Id : null;
        var templateId = preserveSelection ? (TemplatePicker.SelectedItem as ProjectTemplate)?.Id : null;
        var search = preserveSelection ? DeviceSearch.Text : "";
        // 在线包可能与本地包有不同模板；两个版本都可选，旧工程与旧模板不因同步而消失。
        installedPacks = catalog
            .OrderBy(p => p.Manifest.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(p => p.Manifest.Version, Comparer<string>.Create(ComparePackVersions)).ToArray();
        VendorPicker.SelectedIndex = -1;
        VendorPicker.ItemsSource = installedPacks.Select(PackManufacturer)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select(ManufacturerOption.FromId).ToArray();
        VendorPicker.IsEnabled = installedPacks.Length > 0;
        FilterPacks();
        if (preserveSelection && vendorId is not null)
        {
            VendorPicker.SelectedItem = VendorPicker.Items.Cast<ManufacturerOption>()
                .FirstOrDefault(vendor => vendor.Id == vendorId);
            if (packId is not null)
            {
                PackPicker.SelectedItem = PackPicker.Items.Cast<InstalledPack>()
                    .FirstOrDefault(pack => pack.Manifest.Id == packId && pack.Manifest.Version == packVersion)
                    ?? PackPicker.Items.Cast<InstalledPack>().FirstOrDefault(pack => pack.Manifest.Id == packId);
                DeviceSearch.Text = search;
                DevicePicker.SelectedItem = DevicePicker.Items.Cast<DeviceDefinition>()
                    .FirstOrDefault(device => device.Id == deviceId);
                TemplatePicker.SelectedItem = TemplatePicker.Items.Cast<ProjectTemplate>()
                    .FirstOrDefault(template => template.Id == templateId);
            }
        }
    }
    private static string PackManufacturer(InstalledPack pack)
    {
        var vendor = pack.Manifest.Vendor.Trim();
        // 仅归一化已知包的厂商署名；不按型号猜厂商，也不改写包内容。
        if (vendor.Equals("STMicroelectronics / StudioX templates", StringComparison.OrdinalIgnoreCase))
            return "STMicroelectronics";
        if (vendor.Equals("STC / 宏晶科技", StringComparison.OrdinalIgnoreCase))
            return "STC";
        return vendor;
    }
    private static int ComparePackVersions(string left, string right)
    {
        // Pack 格式要求三段非负整数；按数值比较，避免字符串排序错误和整数溢出。
        var a = left.Split('.'); var b = right.Split('.');
        for (var i = 0; i < 3; i++)
        {
            var result = a[i].Length.CompareTo(b[i].Length);
            if (result == 0) result = string.CompareOrdinal(a[i], b[i]);
            if (result != 0) return result;
        }
        return 0;
    }
    private void VendorPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PackPicker is null || PackageStatus is null) return;
        FilterPacks();
    }
    private void FilterPacks()
    {
        var manufacturer = VendorPicker.SelectedItem as ManufacturerOption;
        var vendor = manufacturer?.Id;
        PackPicker.SelectedIndex = -1;
        PackPicker.ItemsSource = installedPacks.Where(p =>
            string.Equals(PackManufacturer(p), vendor, StringComparison.OrdinalIgnoreCase)).ToArray();
        PackPicker.IsEnabled = PackPicker.Items.Count > 0;
        DeviceSearch.Clear();
        FilterDevices();
        PackageStatus.Text = installedPacks.Length == 0
            ? "尚未安装器件包。请导入 StudioX 格式 1 的 .mcupack。"
            : vendor is null
                ? $"已安装 {VendorPicker.Items.Count} 家厂商的 {installedPacks.Length} 个器件包。请先选择器件厂商。"
                : $"{manufacturer!.DisplayName} · {PackPicker.Items.Count} 个器件包。请选择器件包、芯片型号与工程模板。";
    }
    private void SelectPack(InstalledPack? pack)
    {
        VendorPicker.SelectedItem = pack is null ? null : VendorPicker.Items.Cast<ManufacturerOption>()
            .Single(v => string.Equals(v.Id, PackManufacturer(pack), StringComparison.OrdinalIgnoreCase));
        PackPicker.SelectedItem = pack is null ? null : PackPicker.Items.Cast<InstalledPack>()
            .Single(p => p.Manifest.Id == pack.Manifest.Id && p.Manifest.Version == pack.Manifest.Version);
    }
    private async Task<InstalledPack> ImportPackForSelectionAsync(string archive, CancellationToken token)
    {
        var pack = await services.Packs.ImportAsync(archive, token);
        await RefreshPacksAsync(token);
        var current = installedPacks.Single(p => p.Manifest.Id == pack.Manifest.Id && p.Manifest.Version == pack.Manifest.Version);
        SelectPack(current);
        PackageStatus.Text = $"已导入 {pack.Manifest.DisplayName} {pack.Manifest.Version}。请选择芯片和模板。";
        return current;
    }
    private async void ImportPack_Click(object sender, RoutedEventArgs e)
    {
        if (projectDirectory is not null) await ShowProjectDetailsAsync();
        else { SetProjectDetailsMode(null); ShowDocument(PackagesTab); await RunAsync(token => RefreshPacksAsync(token)); }
        var dialog = new OpenFileDialog { Filter = "StudioX 芯片包|*.mcupack" };
        if (dialog.ShowDialog(this) != true) return;
        await RunAsync(async token =>
        {
            if (projectDirectory is null)
            {
                await ImportPackForSelectionAsync(dialog.FileName, token);
                Log(PackageStatus.Text);
            }
            else
            {
                var pack = await services.Packs.ImportAsync(dialog.FileName, token);
                Status.Text = $"已导入 {pack.Manifest.DisplayName} {pack.Manifest.Version}，可在新建工程时选择。当前工程配置保持不变。";
                Log(Status.Text);
            }
        });
    }
    private void PackPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DevicePicker is null || TemplatePicker is null) return;
        if (DeviceSearch is not null) DeviceSearch.Clear();
        FilterDevices();
    }
    private void DeviceSearch_TextChanged(object sender, TextChangedEventArgs e) => FilterDevices();
    private void FilterDevices()
    {
        if (DevicePicker is null || DeviceSearch is null || TemplatePicker is null || CreateProjectButton is null) return;
        var query = DeviceSearch.Text.Trim();
        var pack = PackPicker.SelectedItem as InstalledPack;
        var devices = pack?.Manifest.Devices;
        var isPuya = string.Equals(pack?.Manifest.Vendor, "Puya", StringComparison.OrdinalIgnoreCase);
        var isStc = pack is not null && string.Equals(PackManufacturer(pack), "STC", StringComparison.OrdinalIgnoreCase);
        var searchHint = isStc ? "搜索型号，例如 IAP15F2K61S2；请以器件包中的完整型号为准。" : "搜索型号，例如 F103C8；封装和温度后缀可省略。";
        DeviceSearchHint.Text = searchHint;
        DeviceSearch.ToolTip = searchHint;
        DeviceSearch.IsEnabled = devices is not null;
        DevicePicker.ItemsSource = devices?.Where(d => query.Length == 0 ||
            d.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            query.StartsWith(d.Id, StringComparison.OrdinalIgnoreCase) ||
            isPuya && MatchesPuyaPackageAlias(d.Id, query)).ToArray();
        // 只过滤厂商目录，仍需明确选择实际型号，不从自由文本推断芯片。
        DevicePicker.SelectedIndex = -1;
        DevicePicker.IsEnabled = DevicePicker.Items.Count > 0;
        TemplatePicker.ItemsSource = null;
        TemplatePicker.IsEnabled = false;
        CreateProjectButton.IsEnabled = false;
        UpdateAg32LogicModeOption();
    }
    private static bool MatchesPuyaPackageAlias(string deviceId, string query)
    {
        // 普冉 DFP 的 x 可代表 K2/C1 等引脚变体；完整订货号仍按容量码匹配到 DFP 器件。
        var wildcard = deviceId.LastIndexOf('x');
        if (wildcard < 0 || wildcard == deviceId.Length - 1) return false;
        var normalized = query.Length > 0 && (query[0] == 'F' || query[0] == 'f') ? "PY32" + query : query;
        if (!normalized.StartsWith(deviceId[..wildcard], StringComparison.OrdinalIgnoreCase)) return false;
        var remainder = normalized[wildcard..];
        if (remainder.Length <= 2) return remainder.Length > 0 && char.IsLetter(remainder[0]);
        var capacity = deviceId[(wildcard + 1)..];
        return Enumerable.Range(1, 2).Any(pinCodeLength =>
            remainder.Length >= pinCodeLength + capacity.Length &&
            remainder.AsSpan(pinCodeLength, capacity.Length).Equals(capacity.AsSpan(), StringComparison.OrdinalIgnoreCase));
    }
    private void DevicePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TemplatePicker is null || CreateProjectButton is null) return;
        var device = DevicePicker.SelectedItem as DeviceDefinition;
        TemplatePicker.ItemsSource = device?.Templates;
        TemplatePicker.SelectedIndex = -1;
        TemplatePicker.IsEnabled = TemplatePicker.Items.Count > 0;
        CreateProjectButton.IsEnabled = false;
        if (SelectedDeviceCapability is not null)
        {
            var pack = PackPicker.SelectedItem as InstalledPack;
            var vendor = pack is null ? null : PackManufacturer(pack);
            var isStc = string.Equals(vendor, "STC", StringComparison.OrdinalIgnoreCase);
            SelectedDeviceCapability.Visibility = device is { OpenOcd: null } &&
                (string.Equals(vendor, "Puya", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(vendor, "GigaDevice", StringComparison.OrdinalIgnoreCase) || isStc)
                ? Visibility.Visible : Visibility.Collapsed;
            SelectedDeviceCapability.Text = isStc
                ? $"{device?.Id ?? "STC 8 位器件"} · SDCC + CMake + Ninja；可创建和编译工程，下载与调试暂未接入。"
                : "可创建和编译工程；下载与调试待实板验证，暂未启用。";
        }
        UpdateAg32LogicModeOption();
    }
    private void UpdateAg32LogicModeOption()
    {
        if (Ag32LogicModePanel is null || Ag32LogicModeCheckBox is null) return;
        var show = PackPicker.SelectedItem is InstalledPack pack &&
            DevicePicker.SelectedItem is DeviceDefinition device && ProjectService.IsAg32LogicDevice(pack, device.Id);
        if (!show) Ag32LogicModeCheckBox.IsChecked = false;
        Ag32LogicModePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }
    private void TemplatePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CreateProjectButton is not null) CreateProjectButton.IsEnabled = TemplatePicker.SelectedItem is ProjectTemplate;
    }
    private async void CreateProject_Click(object sender, RoutedEventArgs e)
    {
        if (projectDirectory is not null) { await ShowProjectDetailsAsync(); return; }
        if (PackPicker.SelectedItem is not InstalledPack pack || DevicePicker.SelectedItem is not DeviceDefinition device || TemplatePicker.SelectedItem is not ProjectTemplate template)
        { Status.Text = "请先明确选择器件厂商、芯片包、器件型号和模板。"; return; }
        var enableAg32Logic = Ag32LogicModeCheckBox.IsChecked == true;
        var dialog = new OpenFolderDialog { Title = "选择新工程的父目录" };
        if (dialog.ShowDialog(this) != true) return;
        await RunAsync(async token =>
        {
            PackValidator.Token(ProjectName.Text);
            var destination = Path.Combine(dialog.FolderName, ProjectName.Text);
            var created = await services.Projects.CreateAsync(pack, device.Id, template.Id, ProjectName.Text, destination, token, enableAg32Logic);
            Log("工程已创建，Git 仓库已初始化（main 分支）；请在工程终端设置身份并提交。");
            await OpenProjectAsync(destination, token);
            if (created.Logic is { } logic) await OpenSourceAsync(logic.VerilogFile, token);
        });
    }
    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 StudioX 工程或 CubeMX CMake 工程目录" };
        if (dialog.ShowDialog(this) == true) await RunAsync(token => File.Exists(Path.Combine(dialog.FolderName, ".studiox/project.json"))
            ? OpenProjectAsync(dialog.FolderName, token) : ImportCubeMxAsync(dialog.FolderName, token));
    }
    private async Task OpenProjectAsync(string directory, CancellationToken token)
    {
        directory = Path.GetFullPath(directory);
        if (string.Equals(projectDirectory, directory, StringComparison.OrdinalIgnoreCase))
        {
            if (activeEditor is not null) ShowDocument(activeEditor.Tab);
            return;
        }
        if (!await ConfirmDocumentsAsync()) return;
        await PersistBreakpointLinesAsync();
        await services.Debugger.StopAsync(); await debugNavigationTask;
        debugAnchors.Clear(); lastDebugSnapshot = null;
        var project = await ProjectService.ReadAsync(directory, token);
        var mainPath = project.Kind == ProjectKind.CubeMx ? "Core/Src/main.c" : "src/main.c";
        var source = services.Files.FileExists(directory, mainPath) ? await services.Files.ReadAsync(directory, mainPath, token) : null;
        CloseCodeAssistance();
        await assistTask;
        await Task.WhenAll(hoverTask, navigationTask);
        await services.Intelligence.StopAsync();
        ClearEditorDocuments();
        projectDirectory = directory;
        GitGraph.SetProject(directory);
        GitHubWorkspace.SetProject(directory);
        BuildMemory.SetMessage("正在读取上次构建的占用…");
        SetProjectDetailsMode(project);
        await ProjectTerminal.SetProjectAsync(directory);
        await services.Debugger.OpenProjectAsync(directory, token);
        ApplyDownloadConfiguration(null);
        // 新建入口负责关闭确认；器件与模板入口仅展示当前工程配置。
        PackagesTab.Visibility = Visibility.Collapsed;
        ProjectLabel.Text = directory;
        WindowProjectTitle.Text = project.Name;
        Title = project.Name + " — MCU StudioX";
        BuildConfiguration.Text = project.Name + " · " + (project.CubeMx?.ConfigurePreset ?? project.CubeMx?.BuildType ?? "Debug");
        DeviceLabel.Text = "器件 / " + project.DeviceId;
        ToolsetLabel.Text = $"工具集 / {project.ToolsetId} {project.ToolsetVersion}";
        ApplyDownloadConfiguration(await services.Downloads.ConfigurationAsync(directory, token));
        if (source is not null) ShowSource(source); else ShowDocument(WelcomeTab);
        // 首次挂入编辑器会触发布局与语法渲染，先处理输入和这一帧，再展开工程树，避免累计成一次长停顿。
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        PopulateProjectTree(project.Name);
        await RefreshBuildMemoryAsync(directory, token);
        await services.RecentProjects.RememberAsync(project.Name, directory, token);
        await RefreshRecentAsync(token);
        Status.Text = "正在准备代码提示…";
        try
        {
            if (project.Kind == ProjectKind.CubeMx && !await ConfigureCubeMxAsync(directory, token))
            { Status.Text = "工程已打开；CMake 配置失败，请查看构建日志并修正工程配置。"; return; }
            Status.Text = "正在准备代码索引与提示…";
            await services.Intelligence.StartAsync(directory, token);
            Status.Text = IsStcSdccProject ? "已打开 " + project.Name + " · 通用 C 代码提示已就绪；8051 扩展语义以 SDCC 编译为准"
                : "已打开 " + project.Name + " · C/C++ 代码提示已就绪";
            QueueOutlineRefresh(clear: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log(ex.ToString()); Status.Text = "工程已打开；代码提示不可用：" + ex.Message; }
    }
    private async void Save_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (activeEditor is not { } session) { Status.Text = "没有需要保存的文件。"; return; }
        var directory = RequireProject();
        await SaveEditorAsync(directory, session, token); Status.Text = "已保存 " + session.Source.RelativePath;
    });
    private async void VerifyTools_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        ShowDocument(ExtensionsTab);
        ToolInventory.Text = "正在校验文件并检查工具版本…";
        ToolInventory.Text = await services.ToolInventory.DescribeAsync(verify: true, new Progress<string>(message => Status.Text = message), token);
        Status.Text = "工具检查完成，结果见插件与工具集页面。";
    });
    private string RequireProject() => projectDirectory ?? throw new StudioXException("PROJECT_REQUIRED", "请先创建或打开工程。");
    private async void Build_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        EnsureNoActiveDebug();
        var directory = RequireProject();
        await SaveAllSourcesAsync(directory, token);
        ShowBottom(0);
        BuildLog.Clear();
        var report = await BuildWithSummaryAsync(directory, token);
        if (report.Success && (await ProjectService.ReadAsync(directory, token)).Kind == ProjectKind.CubeMx)
            await RefreshExplorerLanguageAsync(token);
        if (report.LogPath is { } logPath) Log("构建日志：" + logPath);
        foreach (var artifact in report.Artifacts) Log(artifact);
        Log(report.Summary);
        Status.Text = report.Summary;
    });
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (GitGraph.IsMutating) { Status.Text = "请等待 Git 操作完成。"; return; }
        operationCancellation?.Cancel();
    }

    private async void Simulation_Click(object sender, RoutedEventArgs e) => await RunAsync(async _ =>
    {
        if (simulation is null) await StartSimulationAsync(); else await StopSimulationAsync();
    });
    private async Task StartSimulationAsync()
    {
        simulation = await services.Devices.OpenAsync(new SimulationTransport());
        ShowBottom(1);
        simulationCancellation = new CancellationTokenSource();
        logSubscription = simulation.Subscribe(128); plotSubscription = simulation.Subscribe(128);
        logFrames = plotFrames = 0; Plot.Clear(); DeviceLog.Clear();
        consumers = [ConsumeLogAsync(logSubscription, simulationCancellation.Token), ConsumePlotAsync(plotSubscription, simulationCancellation.Token)];
        SimulationButton.Content = "断开模拟设备"; RecordButton.IsEnabled = true;
        Status.Text = "模拟设备运行中 · 未连接真实串口";
    }
    private async Task ConsumeLogAsync(FrameSubscription subscription, CancellationToken token)
    {
        try
        {
            await foreach (var frame in subscription.Reader.ReadAllAsync(token))
            {
                logFrames++;
                DeviceLog.AppendText($"#{frame.Sequence:D6}   " + Encoding.UTF8.GetString(frame.Payload.Span));
                if (DeviceLog.Text.Length > 12000) DeviceLog.Text = DeviceLog.Text[^8000..];
                DeviceLog.ScrollToEnd();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Log("设备日志：" + ex); }
    }
    private async Task ConsumePlotAsync(FrameSubscription subscription, CancellationToken token)
    {
        try
        {
            await foreach (var frame in subscription.Reader.ReadAllAsync(token))
            {
                plotFrames++;
                var parts = Encoding.UTF8.GetString(frame.Payload.Span).Trim().Split(',');
                if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var value) && int.TryParse(parts[1], out var state)) Plot.Add(value, state);
                FrameStatus.Text = $"模拟 · 帧 {frame.Sequence} · 丢帧 {subscription.DroppedFrames + (logSubscription?.DroppedFrames ?? 0)}";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Log("设备绘图：" + ex); }
    }
    private async Task StopSimulationAsync()
    {
        await StopCaptureAsync();
        if (simulationCancellation is not null) await simulationCancellation.CancelAsync();
        if (simulation is not null) await simulation.DisposeAsync();
        await Task.WhenAll(consumers);
        logSubscription?.Dispose(); plotSubscription?.Dispose(); simulationCancellation?.Dispose();
        simulation = null; simulationCancellation = null; consumers = [];
        SimulationButton.Content = "连接模拟设备"; RecordButton.IsEnabled = false; FrameStatus.Text = "已断开";
    }
    private async void Record_Click(object sender, RoutedEventArgs e) => await RunAsync(async _ =>
    {
        if (captureTask is not null) { await StopCaptureAsync(); return; }
        if (simulation is null) return;
        var dialog = new SaveFileDialog { Filter = "StudioX 记录|*.sxcapture", FileName = "capture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".sxcapture" };
        if (dialog.ShowDialog(this) != true) return;
        if (File.Exists(dialog.FileName)) throw new StudioXException("CAPTURE_EXISTS", "记录器不覆盖已有文件，请使用新文件名。");
        captureSubscription = simulation.Subscribe(1024); captureCancellation = new CancellationTokenSource();
        captureTask = CaptureFile.WriteAsync(dialog.FileName, simulation, captureSubscription, captureCancellation.Token);
        RecordButton.Content = "停止记录"; Status.Text = "记录中：" + dialog.FileName;
    });
    private async Task StopCaptureAsync()
    {
        if (captureCancellation is not null) await captureCancellation.CancelAsync();
        if (captureTask is not null)
        {
            try { await captureTask; }
            catch (OperationCanceledException) when (captureCancellation?.IsCancellationRequested == true) { }
            catch (Exception ex) { Log("记录失败：" + ex); }
            Log($"记录停止；丢帧 {captureSubscription?.DroppedFrames ?? 0}。");
        }
        captureSubscription?.Dispose(); captureCancellation?.Dispose();
        captureTask = null; captureSubscription = null; captureCancellation = null; RecordButton.Content = "开始记录";
    }
    private async void Decode_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (PluginPicker.SelectedItem is not string manifest) throw new StudioXException("PLUGIN_REQUIRED", "请先选择解码插件。");
        var result = await services.Plugins.DecodeAsync(manifest, Encoding.UTF8.GetBytes(PluginInput.Text), token);
        PluginOutput.Text = JsonSerializer.Serialize(result, JsonStore.Options);
    });

    private Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (!pendingOperation.IsCompleted || closing) { Status.Text = "请等待当前操作完成。"; return Task.CompletedTask; }
        pendingOperation = RunCoreAsync(action);
        return pendingOperation;
    }
    private async Task RunCoreAsync(Func<CancellationToken, Task> action)
    {
        operationCancellation = new CancellationTokenSource();
        UpdateProjectActions(busy: true); CancelButton.IsEnabled = true;
        try { await action(operationCancellation.Token); }
        catch (OperationCanceledException) { Status.Text = "操作已取消"; Log(Status.Text); }
        catch (Exception ex)
        {
            Status.Text = ex is StudioXException studio ? studio.Code + "：" + studio.Message : ex.Message;
            Log(ex.ToString());
        }
        finally { operationCancellation.Dispose(); operationCancellation = null; UpdateProjectActions(busy: false); CancelButton.IsEnabled = false; }
    }
    private void Log(string text)
    {
        BuildLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\n");
        if (BuildLog.Text.Length > 500000) BuildLog.Text = "[较早日志已截断]\n" + BuildLog.Text[^400000..];
        BuildLog.ScrollToEnd();
    }
    // 供以后启用的桌面冒烟检查使用；普通启动不会自动连接模拟器。
    public async Task ExerciseSimulationAsync()
    {
        ShowDocument(LabTab);
        await StartSimulationAsync();
        await Task.Delay(1800);
        if (logFrames < 2 || plotFrames < 2) throw new InvalidOperationException("模拟订阅未收到数据。");
        await StopSimulationAsync();
    }
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (closed) return;
        e.Cancel = true;
        if (closing) return;
        closing = true; IsEnabled = false;
        packSyncCancellation?.Cancel();
        CancelOutline();
        CloseCodeAssistance();
        if (!GitGraph.IsMutating) operationCancellation?.Cancel();
        var documentAccepted = false;
        try
        {
            await pendingOperation;
            await StopPackSyncAsync();
            await pendingZoomSave;
            breakpointSaveTimer.Stop(); await PersistBreakpointLinesAsync();
            await services.Debugger.StopAsync(); await debugNavigationTask;
            await assistTask;
            await outlineTask;
            await Task.WhenAll(hoverTask, navigationTask);
            if (!await ConfirmDocumentsAsync()) { closing = false; IsEnabled = true; QueueOutlineRefresh(); return; }
            documentAccepted = true;
            await SerialView.ShutdownAsync();
            await SerialPlotView.ShutdownAsync();
            await ProjectTerminal.ShutdownAsync();
            BackgroundVideo.Close();
            await StopSimulationAsync(); await services.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log("关闭清理：" + ex);
            if (!documentAccepted) { closing = false; IsEnabled = true; QueueOutlineRefresh(); Status.Text = "未能保存修改，窗口保持打开。"; }
        }
        finally { if (closing) { closed = true; _ = Dispatcher.BeginInvoke(new Action(Close)); } }
    }
}
