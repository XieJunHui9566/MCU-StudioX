namespace StudioX.Desktop;

using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using StudioX.Application;
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
    private bool closing;
    private bool closed;
    private readonly ProjectTransitionCoordinator projectTransitions;

    public MainWindow(WorkbenchService services)
    {
        this.services = services;
        InitializeComponent();
        projectTransitions = CreateProjectTransitionCoordinator();
        InitializeSimulation();
        InitializeBuildSettings();
        InitializeEspressifModuleSettings();
        InitializeStcIsp();
        InitializeLvglPreview();
        InitializeAg32PinPlanning();
        InitializeHdlSchematic();
        InitializeHdlWorkflow();
        SerialView.Attach(services.Serial);
        MicroPythonPanel.Attach(services.MicroPython, services.Files);
        MicroPythonPanel.DownloadRequested = () => RunAsync(token => DownloadMicroPythonAsync(RequireProject(), token));
        MicroPythonPanel.StateChanged += () => UpdateProjectActions(projectActionsBusy);
        SerialPlotView.Attach(services.SerialPlot);
        OpenOcdPlotPanel.Attach(services.OpenOcdPlot);
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
        InitializePlugins();
        InitializeSplitEditors();
        InitializeProductivity();
        Activated += (_, _) => QueueRecentPrune();
    }

    public async Task InitializeAsync(bool loadGitHubAccounts = true)
    {
        editorPersistenceEnabled = loadGitHubAccounts;
        if (loadGitHubAccounts)
        {
            try
            {
                await LoadWorkbenchLayoutAsync();
            }
            catch (Exception ex) { Log("布局恢复失败：" + ex); }
        }
        await RunAsync(async token =>
        {
            // 首页记录与器件仓库无关，优先显示；不扫描工程目录或校验所有 SDK。
            try
            {
                await RefreshRecentAsync(token);
            }
            catch (Exception ex) { Log("最近工程读取失败：" + ex.Message); }
            try
            {
                ApplyTheme(await services.Themes.LoadAsync(token));
            }
            catch (Exception ex) { Log("主题恢复失败，使用默认主题：" + ex.Message); ApplyTheme(ThemeService.Dark); }
            try
            {
                ApplyEditorSettings(await services.EditorSettings.LoadAsync(token));
            }
            catch (Exception ex) { Log("编辑器设置恢复失败：" + ex.Message); ApplyEditorSettings(new()); }
            try
            {
                ApplyBackground(await services.Appearance.LoadAsync(token));
            }
            catch (Exception ex) { Log("背景恢复失败：" + ex.Message); Status.Text = "背景不可用，可在外观设置中重新选择。"; }
            ToolInventory.Text = await services.ToolInventory.DescribeAsync(token);
            PluginPicker.ItemsSource = services.PluginManifests.ToArray();
            await PluginManager.RefreshAsync(token);
            try
            {
                // 隔离截图与启动验证不读取系统凭据，避免将真实账号带入公开图片。
                if (loadGitHubAccounts)
                {
                    await GitHubWorkspace.RefreshAccountsForChromeAsync(token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { Log("GitHub 账号状态读取失败：" + ex.Message); }
        });
        QueueRecentPrune();
    }

    public void ApplyTheme(ThemeDefinition theme)
    {
        ThemeService.Validate(theme);
        foreach (var (key, value) in theme.Colors)
        {
            System.Windows.Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        }
        System.Windows.Application.Current.Resources[SystemColors.ControlBrushKey] = System.Windows.Application.Current.Resources["Surface"];
        currentTheme = theme;
        DiagnosticPalette.Apply(theme);
        RefreshSurfaceBrushes();
        ApplyEditorTheme();
        Plot.InvalidateVisual();
        SerialPlotView.RefreshTheme();
        OpenOcdPlotPanel.RefreshTheme();
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
                if (result.Imported > 0)
                {
                    Log($"已导入 {result.Imported} 个随附器件包。");
                }
                foreach (var failure in result.Failures)
                {
                    Log($"随附器件包导入失败：{failure.File}：{failure.Message}");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                bundledPacksChecked = true;
                Log("读取随附器件包失败，可手动导入：" + ex.Message);
            }
        }
        var cleanup = await services.Packs.PruneSupersededAsync(token);
        if (cleanup.Removed.Count > 0)
        {
            Log($"已清理 {cleanup.Removed.Count} 个重复旧器件包，释放 {cleanup.ReclaimedBytes / 1024d / 1024:F1} MiB。");
        }
        foreach (var failure in cleanup.Failures)
        {
            Log($"旧器件包清理失败：{failure.Id} {failure.Version}：{failure.Message}");
        }
        var catalog = await services.Packs.ListCatalogAsync(token);
        // 目录读取期间允许用户继续选择；在实际重建列表前采集最新选择。
        var vendorId = preserveSelection ? (VendorPicker.SelectedItem as ManufacturerOption)?.Id : null;
        var packId = preserveSelection ? (PackPicker.SelectedItem as InstalledPack)?.Manifest.Id : null;
        var packVersion = preserveSelection ? (PackPicker.SelectedItem as InstalledPack)?.Manifest.Version : null;
        var deviceId = preserveSelection ? (DevicePicker.SelectedItem as DeviceDefinition)?.Id : null;
        var templateId = preserveSelection ? (TemplatePicker.SelectedItem as ProjectTemplate)?.Id : null;
        var search = preserveSelection ? DeviceSearch.Text : "";
        // 冗余旧版已清理；只有新版不覆盖的旧型号或模板继续保留，不能因版本更高而丢失 SPL 等能力。
        installedPacks = catalog
            .OrderBy(p => p.Manifest.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(p => p.Manifest.Version, Comparer<string>.Create(PackVersion.Compare)).ToArray();
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
                    ?? PackPicker.Items.Cast<InstalledPack>().Where(pack => pack.Manifest.Id == packId)
                        .OrderByDescending(pack => pack.Manifest.Version, Comparer<string>.Create(PackVersion.Compare)).FirstOrDefault();
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
        {
            return "STMicroelectronics";
        }
        if (vendor.Equals("STC / 宏晶科技", StringComparison.OrdinalIgnoreCase))
        {
            return "STC";
        }
        return vendor;
    }
    private void VendorPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PackPicker is null || PackageStatus is null)
        {
            return;
        }
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
        var current = installedPacks.SingleOrDefault(p => p.Manifest.Id == pack.Manifest.Id && p.Manifest.Version == pack.Manifest.Version)
            ?? installedPacks.Where(p => PackCatalogPolicy.Supersedes(p.Manifest, pack.Manifest))
                .OrderByDescending(p => p.Manifest.Version, Comparer<string>.Create(PackVersion.Compare)).First();
        SelectPack(current);
        PackageStatus.Text = current.Manifest.Version == pack.Manifest.Version
            ? $"已导入 {current.Manifest.DisplayName} {current.Manifest.Version}。请选择芯片和模板。"
            : $"{pack.Manifest.Version} 已被新版完整覆盖，保留 {current.Manifest.DisplayName} {current.Manifest.Version}。请选择芯片和模板。";
        return current;
    }
    private async void ImportPack_Click(object sender, RoutedEventArgs e)
    {
        if (projectDirectory is not null)
        {
            await ShowProjectDetailsAsync();
        }
        else
        {
            SetProjectDetailsMode(null);
            ShowDocument(PackagesTab);
            await RunAsync(token => RefreshPacksAsync(token));
        }
        var dialog = new OpenFileDialog { Filter = "StudioX 芯片包|*.mcupack" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
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
        if (DevicePicker is null || TemplatePicker is null)
        {
            return;
        }
        if (DeviceSearch is not null)
        {
            DeviceSearch.Clear();
        }
        FilterDevices();
    }
    private void DeviceSearch_TextChanged(object sender, TextChangedEventArgs e) => FilterDevices();
    private void FilterDevices()
    {
        if (DevicePicker is null || DeviceSearch is null || TemplatePicker is null || CreateProjectButton is null)
        {
            return;
        }
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
        ClearIdfVersionSelection();
    }
    private static bool MatchesPuyaPackageAlias(string deviceId, string query)
    {
        // 普冉 DFP 的 x 可代表 K2/C1 等引脚变体；完整订货号仍按容量码匹配到 DFP 器件。
        var wildcard = deviceId.LastIndexOf('x');
        if (wildcard < 0 || wildcard == deviceId.Length - 1)
        {
            return false;
        }
        var normalized = query.Length > 0 && (query[0] == 'F' || query[0] == 'f') ? "PY32" + query : query;
        if (!normalized.StartsWith(deviceId[..wildcard], StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var remainder = normalized[wildcard..];
        if (remainder.Length <= 2)
        {
            return remainder.Length > 0 && char.IsLetter(remainder[0]);
        }
        var capacity = deviceId[(wildcard + 1)..];
        return Enumerable.Range(1, 2).Any(pinCodeLength =>
            remainder.Length >= pinCodeLength + capacity.Length &&
            remainder.AsSpan(pinCodeLength, capacity.Length).Equals(capacity.AsSpan(), StringComparison.OrdinalIgnoreCase));
    }
    private void DevicePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TemplatePicker is null || CreateProjectButton is null)
        {
            return;
        }
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
        ClearIdfVersionSelection();
    }
    private void UpdateAg32LogicModeOption()
    {
        if (Ag32LogicModePanel is null || Ag32LogicModeCheckBox is null)
        {
            return;
        }
        var show = PackPicker.SelectedItem is InstalledPack pack &&
            DevicePicker.SelectedItem is DeviceDefinition device && ProjectService.IsAg32LogicDevice(pack, device.Id);
        if (!show)
        {
            Ag32LogicModeCheckBox.IsChecked = false;
        }
        Ag32LogicModeCheckBox.IsEnabled = show;
        if (show && DevicePicker.SelectedItem is DeviceDefinition selectedDevice && Ag32DeviceCatalog.Find(selectedDevice.Id) is { } profile)
        {
            Ag32LogicDeviceHint.Text = $"{profile.DeviceId} · {profile.TargetDevice} / {profile.PackageName}";
            Ag32LogicModeDescription.Text = "默认创建普通 MCU 工程；勾选后，在所选模板基础上加入 FPGA 逻辑。顶部编译联合生成 MCU 固件与 FPGA 位流，使用内置原生综合和 Supra 布局布线工具。Supra 需配置本机有效许可。";
        }
        Ag32LogicModePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void TemplatePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAg32LogicModeOption();
        if (CreateProjectButton is not null)
        {
            CreateProjectButton.IsEnabled = TemplatePicker.SelectedItem is ProjectTemplate;
        }
        UpdateSelectedComponents();
        await RefreshIdfVersionsAsync();
    }
    private void ComponentSelection_Changed(object sender, RoutedEventArgs e) => UpdateSelectedComponents();
    private void UpdateSelectedComponents()
    {
        if (SelectedDevelopmentComponents is null) return;
        SelectedDevelopmentComponents.Text = PackPicker?.SelectedItem is InstalledPack pack &&
            DevicePicker?.SelectedItem is DeviceDefinition device && TemplatePicker?.SelectedItem is ProjectTemplate template
            ? ProjectComponentSummary.ForSelection(pack, device.Id, template.Id, Ag32LogicModeCheckBox?.IsChecked == true) : "";
    }
    private async void CreateProject_Click(object sender, RoutedEventArgs e)
    {
        if (projectDirectory is not null)
        {
            await ShowProjectDetailsAsync();
            return;
        }
        if (PackPicker.SelectedItem is not InstalledPack pack || DevicePicker.SelectedItem is not DeviceDefinition device || TemplatePicker.SelectedItem is not ProjectTemplate template)
        {
            Status.Text = "请先明确选择器件厂商、芯片包、器件型号和模板。";
            return;
        }
        var enableAg32Logic = Ag32LogicModeCheckBox.IsChecked == true;
        var idfSelection = IdfVersionPicker.SelectedItem as StudioX.Application.Tools.EspressifProjectVersionChoice;
        if (device.Espressif?.Framework == "esp-idf" && idfSelection?.CanCreate != true)
        {
            Status.Text = "请选择可创建的 ESP-IDF 开发环境组件版本。";
            return;
        }
        var dialog = new OpenFolderDialog { Title = "选择新工程的父目录" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        await RunAsync(async token =>
        {
            if (idfSelection is not null) await services.EspressifProjectVersions.EnsureSelectionAsync(idfSelection, token);
            PackValidator.Token(ProjectName.Text);
            var destination = Path.Combine(dialog.FolderName, ProjectName.Text);
            var created = await services.Projects.CreateAsync(pack, device.Id, template.Id, ProjectName.Text, destination, token, enableAg32Logic);
            Log("工程已创建，Git 仓库已初始化（main 分支）；请在工程终端设置身份并提交。");
            await OpenProjectAsync(destination, token);
            if (created.Logic is { } logic)
            {
                await OpenSourceAsync(logic.VerilogFile, token);
            }
        });
    }
    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 StudioX 工程或 CubeMX CMake 工程目录" };
        if (dialog.ShowDialog(this) == true)
        {
            await RunAsync(token => File.Exists(Path.Combine(dialog.FolderName, ".studiox/project.json"))
            ? OpenProjectAsync(dialog.FolderName, token) : ImportCubeMxAsync(dialog.FolderName, token));
        }
    }
    private async void Save_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (activeEditor is not { } session)
        {
            Status.Text = "没有需要保存的文件。";
            return;
        }
        var directory = RequireProject();
        await SaveEditorAsync(directory, session, token);
        guideSaved = !session.IsDirty;
        RefreshFirstProjectGuide();
        Status.Text = "已保存 " + session.Source.RelativePath;
    });
    private async void VerifyTools_Click(object sender, RoutedEventArgs e) => await VerifyProjectComponentsAsync();
    private Task VerifyProjectComponentsAsync() => RunAsync(async token =>
    {
        var directory = RequireProject();
        ShowDocument(ExtensionsTab);
        ToolInventory.Text = "正在校验当前工程需要的开发环境组件…";
        ToolInventory.Text = await services.ToolInventory.VerifyProjectAsync(directory, new Progress<string>(message => Status.Text = message), token);
        Status.Text = "当前工程的开发环境组件校验完成。";
    });
    private string RequireProject() => projectDirectory ?? throw new StudioXException("PROJECT_REQUIRED", "请先创建或打开工程。");
    private async void Build_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        EnsureNoActiveDebug();
        var directory = RequireProject();
        await SaveAllSourcesAsync(directory, token);
        ShowBottom(0);
        BuildLog.Clear();
        guideBuild = null;
        RefreshFirstProjectGuide();
        var report = await BuildWithSummaryAsync(directory, token);
        guideBuild = report;
        RefreshFirstProjectGuide();
        if (report.Success && (await ProjectService.ReadAsync(directory, token)).Kind == ProjectKind.CubeMx)
        {
            await RefreshExplorerLanguageAsync(token);
        }
        if (report.LogPath is { } logPath)
        {
            Log("构建日志：" + logPath);
        }
        foreach (var artifact in report.Artifacts)
        {
            Log(artifact);
        }
        Log(report.Summary);
        Status.Text = report.Summary;
    });
    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (GitGraph.IsMutating)
        {
            Status.Text = "请等待 Git 操作完成。";
            return;
        }
        operationCancellation?.Cancel();
        buildMemoryCancellation?.Cancel();
        if (IsMicroPythonProject)
        {
            try
            {
                await MicroPythonPanel.StopAsync();
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
                Status.Text = ex.Message;
            }
        }
    }

    private async void Decode_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (PluginPicker.SelectedItem is not string manifest)
        {
            throw new StudioXException("PLUGIN_REQUIRED", "请先选择解码插件。");
        }
        var result = await services.Plugins.DecodeAsync(manifest, Encoding.UTF8.GetBytes(PluginInput.Text), token);
        PluginOutput.Text = JsonSerializer.Serialize(result, JsonStore.Options);
    });

    private Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (!pendingOperation.IsCompleted || closing)
        {
            Status.Text = "请等待当前操作完成。";
            return Task.CompletedTask;
        }
        pendingOperation = RunCoreAsync(action);
        return pendingOperation;
    }
    private async Task RunCoreAsync(Func<CancellationToken, Task> action)
    {
        operationCancellation = new CancellationTokenSource();
        UpdateProjectActions(busy: true);
        CancelButton.IsEnabled = true;
        try
        {
            await action(operationCancellation.Token);
        }
        catch (OperationCanceledException) { Status.Text = "操作已取消"; Log(Status.Text); }
        catch (Exception ex)
        {
            Status.Text = ex is StudioXException studio ? studio.Code + "：" + studio.Message : ex.Message;
            Log(ex.ToString());
            ShowTroubleshooting(FailureDiagnostic(ex));
        }
        finally { operationCancellation.Dispose(); operationCancellation = null; UpdateProjectActions(busy: false); }
    }
    private void Log(string text)
    {
        BuildLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\n");
        if (BuildLog.Text.Length > 500000)
        {
            BuildLog.Text = "[较早日志已截断]\n" + BuildLog.Text[^400000..];
        }
        BuildLog.ScrollToEnd();
    }
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (closed)
        {
            return;
        }
        e.Cancel = true;
        if (closing)
        {
            return;
        }
        closing = true;
        IsEnabled = false;
        packSyncCancellation?.Cancel();
        aiCancellation?.Cancel();
        pluginWorkspaceCancellation?.Cancel();
        pluginInvocationCancellation?.Cancel();
        CancelOutline();
        CancelFreeRtosRead();
        CancelProjectTreeLoading();
        CancelBuildMemoryRefresh();
        CloseCodeAssistance();
        if (!GitGraph.IsMutating)
        {
            operationCancellation?.Cancel();
        }
        var documentAccepted = false;
        try
        {
            await pendingOperation;
            await buildMemoryRefreshTask;
            await StopPackSyncAsync();
            await pendingZoomSave;
            try
            {
                await agentAccessSaveTask;
            }
            catch (Exception ex) { Log("保存 Agent 授权模式：" + ex); }
            breakpointSaveTimer.Stop();
            await PersistBreakpointLinesAsync();
            await services.Debugger.StopAsync();
            await debugNavigationTask;
            await debugRtosTask;
            await assistTask;
            await outlineTask;
            await Task.WhenAll(hoverTask, navigationTask);
            await StopLiveDiagnosticsAsync();
            if (!await ConfirmDocumentsAsync(retainEditorDrafts: editorPersistenceEnabled))
            {
                closing = false;
                IsEnabled = true;
                QueueOutlineRefresh();
                QueueLiveDiagnostics();
                await ReloadPluginWorkspaceAsync(CancellationToken.None);
                return;
            }
            await editorCheckpointTask;
            if (editorPersistenceEnabled)
            {
                await services.WorkbenchLayout.SaveAsync(CaptureWorkbenchLayout());
            }
            await PersistEditorCheckpointAsync();
            editorCheckpointTimer.Stop();
            diagnosticPoll.Stop();
            documentAccepted = true;
            pluginUiTimer.Stop();
            services.PluginManager.Changed -= PluginCatalog_Changed;
            await StopPluginWorkspaceAsync();
            await PluginManager.ShutdownAsync();
            await DisposeAiMcpSessionAsync();
            await SerialView.ShutdownAsync();
            await MicroPythonPanel.StopAsync();
            await SerialPlotView.ShutdownAsync();
            await OpenOcdPlotPanel.CloseSessionAsync();
            await LvglPreview.ShutdownAsync();
            await ProjectTerminal.ShutdownAsync();
            BackgroundVideo.Close();
            await StopSimulationAsync();
            await services.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log("关闭清理：" + ex);
            if (!documentAccepted)
            {
                closing = false;
                IsEnabled = true;
                QueueOutlineRefresh();
                QueueLiveDiagnostics();
                Status.Text = "未能保存修改，窗口保持打开。";
                await ReloadPluginWorkspaceAsync(CancellationToken.None);
            }
        }
        finally { if (closing) { closed = true; _ = Dispatcher.BeginInvoke(new Action(Close)); } }
    }
}
