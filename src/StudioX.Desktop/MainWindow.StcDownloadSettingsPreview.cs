namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

public partial class MainWindow
{
    /// <summary>使用隔离设置工程覆盖跨系列入口、草稿与保存；不列举或打开硬件。</summary>
    public async Task RenderStcDownloadSettingsPreviewAsync(string directory)
    {
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            if (!value)
            {
                throw new InvalidOperationException(name);
            }
            checks.Add(name);
        }
        var originalSize = System.Windows.Application.Current.Resources["WorkbenchFontSize"];
        var custom = ThemeService.Dark with
        {
            Id = "stc-custom",
            Colors = new(ThemeService.Dark.Colors)
        };
        custom.Colors["Accent"] = "#B8E986";
        custom.Colors["OnAccent"] = "#163020";
        try
        {
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light, custom })
            {
                ApplyTheme(theme);
                System.Windows.Application.Current.Resources["WorkbenchFontSize"] = theme == custom ? 18d : 13d;
                Width = 1300;
                Height = 1000;
                foreach (var target in new[] { "STC89C52RC", "STC12C5A60S2", "STC15F104E", "IAP15L2K61S2", "STC8H8K64U", "AI8051U-34K64" })
                {
                    var mx = target == "AI8051U-34K64";
                    projectDirectory = Path.Combine(directory, "projects", target + "-" + theme.Id);
                    currentProjectManifest = new(1, "StcDownloadSettingsPreview", "stc.stc8", "0.1.6", "", target, "bare",
                        mx ? Mx251Profile.ToolsetId : "stc.sdcc", "1.0.0", mx ? Mx251Profile.CompilerId : "sdcc-4.5.0-15242");
                    ProjectDetailsPanel.DataContext = "离线设置预览";
                    ConfigureBuildSettingsForProject();
                    ConfigureStcIspForProject();
                    ConfigureMx251IspForProject();
                    stcRamLimits = null;
                    if (!mx)
                    {
                        await WriteStcBuildSettingsPreviewProjectAsync(projectDirectory, currentProjectManifest);
                        stcRamLimits = await services.Builds.ReadStcRamLimitsAsync(projectDirectory);
                    }
                    var savedBuild = new ProjectBuildSettings();
                    ApplyBuildSettings(savedBuild);
                    var saved = new StcIspSettings(Port: "COM17", TransferBaud: 57600);
                    if (mx)
                    {
                        ApplyMx251Isp(new(), []);
                    }
                    else
                    {
                        var caps = new StcIspCapabilities(target, [StcClockMode.Preserve, StcClockMode.InternalRc], true, 1000000, 40000000, "离线测试配置");
                        ApplyStcIspSettings(saved, caps, new(true, null, null, "离线验证，不访问串口"), ["COM17"]);
                        Check(SdccMemorySettingsPanel.Visibility == Visibility.Visible && SdccMemoryModelPicker.Items.Count == 4 &&
                            SelectedBuildSettings.MemoryModel == SdccMemoryModel.ProjectDefault,
                            target + ": SDCC memory model has default, small, medium and large choices");
                        BuildOptimizationPicker.SelectedValue = CompilerOptimization.Os;
                        SdccMemoryModelPicker.SelectedValue = SdccMemoryModel.Large;
                        SdccIdataSizeBox.Text = "192";
                        SdccXdataSizeBox.Text = "1024";
                        StcPortPicker.Text = "COM99";
                        Check(stcHardwarePickers.Count == StcHardwareCatalog.For(target).Count, target + ": fields follow protocol profile");
                        StcModifyHardware.IsChecked = true;
                        stcHardwarePickers["watchdog_por_enabled"].SelectedValue = "true";
                        Check(TrySelectedStcIspSettings(out var draft, out _) && draft.ModifyHardware &&
                            draft.Hardware?.WatchdogPowerOn == "true", target + ": hardware overrides included in typed settings");
                        StcModifyHardware.IsChecked = false;
                        Check(TrySelectedStcIspSettings(out draft, out _) && !draft.ModifyHardware &&
                            draft.Hardware?.WatchdogPowerOn == "true", target + ": disabled modifications retain draft without applying it");
                        await ShowStcDownloadSettingsAsync(projectDirectory, default);
                        Check(StcPortPicker.Text == "COM99" && SelectedBuildSettings.Optimization == CompilerOptimization.Os &&
                            SelectedBuildSettings.MemoryModel == SdccMemoryModel.Large &&
                            SelectedBuildSettings.IdataSizeBytes == 192 && SelectedBuildSettings.XdataSizeBytes == 1024 &&
                            loadedBuildSettings == savedBuild && loadedStcIspSettings == saved, target + ": settings entry preserves compiler and port drafts");
                        DevicesAndTemplates_Click(this, new RoutedEventArgs());
                        Check(PackagesTab.IsSelected && SelectedBuildSettings.MemoryModel == SdccMemoryModel.Large &&
                            SdccIdataSizeBox.Text == "192" && SdccXdataSizeBox.Text == "1024",
                            target + ": device page navigation preserves memory model and RAM drafts");
                        await ShowStcDownloadSettingsAsync(projectDirectory, default);
                        Check(StcDownloadTab.IsSelected && SelectedBuildSettings.MemoryModel == SdccMemoryModel.Large &&
                            BuildSettingsStatus.Text == "参数尚未保存。" && SaveStcBuildSettingsButton.IsEnabled,
                            target + ": returning to download settings keeps memory model draft and enables save");
                        DevicesAndTemplates_Click(this, new RoutedEventArgs());
                        foreach (var model in new[] { SdccMemoryModel.Small, SdccMemoryModel.Medium, SdccMemoryModel.Large, SdccMemoryModel.ProjectDefault })
                        {
                            SdccMemoryModelPicker.SelectedValue = model;
                            SaveBuildSettings_Click(SaveBuildSettingsButton, new RoutedEventArgs());
                            await pendingOperation;
                            var reloaded = await services.Builds.LoadSettingsAsync(projectDirectory);
                            Check(reloaded.MemoryModel == model && reloaded.IdataSizeBytes == 192 && reloaded.XdataSizeBytes == 1024 && reloaded == loadedBuildSettings &&
                                !SaveStcBuildSettingsButton.IsEnabled && StcPortPicker.Text == "COM99" && loadedStcIspSettings == saved,
                                target + ": " + model + " saves through build service without changing connection draft");
                            ApplyBuildSettings(reloaded);
                            Check(SdccMemoryModelPicker.SelectedValue is SdccMemoryModel selected && selected == model,
                                target + ": " + model + " restored from persisted settings");
                        }
                        SdccXdataSizeBox.Text = "0";
                        Check(TrySelectedBuildSettings(out var zeroXdata, out _) && zeroXdata.XdataSizeBytes == 0 && SaveBuildSettingsButton.IsEnabled,
                            target + ": zero XDATA is an explicit valid budget instead of a default value");
                        SaveBuildSettings_Click(SaveBuildSettingsButton, new RoutedEventArgs());
                        await pendingOperation;
                        Check((await services.Builds.LoadSettingsAsync(projectDirectory)).XdataSizeBytes == 0,
                            target + ": zero XDATA survives persisted settings");
                        SdccXdataSizeBox.Text = "1024";
                        SaveBuildSettings_Click(SaveBuildSettingsButton, new RoutedEventArgs());
                        await pendingOperation;
                        var savedMemory = loadedBuildSettings!;
                        var invalidRam = new (string Name, System.Windows.Controls.TextBox Box, string Value)[]
                        {
                            ("zero-idata", SdccIdataSizeBox, "0"), ("oversize-idata", SdccIdataSizeBox, "257"),
                            ("oversize-xdata", SdccXdataSizeBox, "2048"), ("invalid-xdata", SdccXdataSizeBox, "invalid")
                        };
                        foreach (var invalid in invalidRam)
                        {
                            invalid.Box.Text = invalid.Value;
                            Check(!TrySelectedBuildSettings(out _, out var ramError) && ramError.Length > 0 &&
                                !SaveBuildSettingsButton.IsEnabled && !SaveStcBuildSettingsButton.IsEnabled,
                                target + ": " + invalid.Name + " rejected inline before saving");
                            await ShowStcDownloadSettingsAsync(projectDirectory, default);
                            DevicesAndTemplates_Click(this, new RoutedEventArgs());
                            Check(invalid.Box.Text == invalid.Value && loadedBuildSettings == savedMemory,
                                target + ": " + invalid.Name + " survives page navigation without overwriting saved settings");
                            ApplyBuildSettings(savedMemory);
                        }
                        var knownRamLimits = stcRamLimits;
                        stcRamLimits = null;
                        ApplyBuildSettings(savedMemory);
                        Check(!TrySelectedBuildSettings(out _, out var missingRamError) && missingRamError.Contains("准确型号") &&
                            SdccIdataLimitText.Text.Contains("尚未取得") && SdccXdataLimitText.Text.Contains("尚未取得") &&
                            !SaveBuildSettingsButton.IsEnabled,
                            target + ": missing RAM metadata is explained inline and does not guess capacity");
                        stcRamLimits = knownRamLimits;
                        ApplyBuildSettings(savedMemory);
                        SdccMemoryModelPicker.SelectedValue = SdccMemoryModel.Large;
                        ResetBuildSettings_Click(this, new RoutedEventArgs());
                        Check(SelectedBuildSettings.MemoryModel == SdccMemoryModel.ProjectDefault &&
                            SelectedBuildSettings.IdataSizeBytes is null && SelectedBuildSettings.XdataSizeBytes is null,
                            target + ": reset restores package-default memory model and RAM limits");
                        if (target == "STC8H8K64U")
                        {
                            StcModifyHardware.IsChecked = true;
                            StcDownloadModePicker.SelectedValue = StcDownloadMode.NativeUsb;
                            Check(TrySelectedStcIspSettings(out draft, out _) && draft.Hardware?.WatchdogPowerOn == "true" &&
                                StcModifyHardware.IsEnabled && stcHardwarePickers["watchdog_prescale"].IsEnabled, "USB qualified fields are editable and persist in settings");
                            Check(!stcHardwarePickers["uart1_remap"].IsEnabled, "USB unqualified field cannot acquire a new override");
                            StcDownloadModePicker.SelectedValue = StcDownloadMode.Serial;
                            Check(stcHardwarePickers["watchdog_por_enabled"].SelectedValue as string == "true", "USB mode switch preserves hardware draft");
                            stcHardwarePickers["uart1_remap"].SelectedValue = "true";
                            StcDownloadModePicker.SelectedValue = StcDownloadMode.NativeUsb;
                            Check(!TrySelectedStcIspSettings(out _, out var error) && error.Contains("UART1") &&
                                stcHardwarePickers["uart1_remap"].IsEnabled, "USB refuses unsupported serial draft and allows clearing it");
                            stcHardwarePickers["uart1_remap"].SelectedValue = "";
                            Check(TrySelectedStcIspSettings(out _, out _) && !stcHardwarePickers["uart1_remap"].IsEnabled, "USB draft correction restores valid settings");
                        }
                    }
                    FocusStcSettings();
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    UpdateLayout();
                    Check(StcDownloadTab.IsSelected && BuildExtendedSettings.Parent == StcBuildExtrasHost &&
                        !BuildSettingsCard.IsAncestorOf(StcIspPanel) && !BuildSettingsCard.IsAncestorOf(Mx251IspPanel), target + ": dedicated page owns transport and build extras");
                    Check(SdccMemorySettingsPanel.Visibility == (mx ? Visibility.Collapsed : Visibility.Visible) &&
                        (mx || BuildSettingsCard.IsAncestorOf(SdccMemorySettingsPanel) && !StcBuildExtrasHost.IsAncestorOf(SdccMemorySettingsPanel)),
                        target + ": SDCC model and RAM fields belong to the device-page compiler card");
                    if (!mx)
                    {
                        SdccMemoryModelPicker.SelectedValue = SdccMemoryModel.Large;
                        DevicesAndTemplates_Click(this, new RoutedEventArgs());
                        SdccMemorySettingsPanel.BringIntoView();
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                        Render(this, Path.Combine(directory, target + "-" + theme.Id + "-memory-model.png"));
                        FocusStcSettings();
                    }
                    if (mx)
                    {
                        Mx251IspPanel.BringIntoView();
                    }
                    else
                    {
                        StcIspPanel.BringIntoView();
                    }
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    Render(this, Path.Combine(directory, target + "-" + theme.Id + "-transport.png"));
                    StcHardwareCard.BringIntoView();
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    var saveBounds = SaveStcHardwareButton.TransformToAncestor(WorkspaceTabs).TransformBounds(new Rect(SaveStcHardwareButton.RenderSize));
                    Check(SaveStcHardwareButton.IsVisible && saveBounds.Top >= 0 && saveBounds.Bottom <= WorkspaceTabs.ActualHeight,
                        target + ": save action stays visible while scrolling long options");
                    if (!mx)
                    {
                        StcModifyHardware.IsChecked = true;
                    }
                    Render(this, Path.Combine(directory, target + "-" + theme.Id + "-hardware.png"));
                }
                await CheckRt51MemoryModelPreviewAsync(directory, theme.Id, Check);
            }
            currentProjectManifest = new(1, "普通 ARM 工程", "test.arm", "1.0.0", "", "STM32F407ZGT6", "bare", "arm.gcc", "1.0.0", "gcc");
            ConfigureBuildSettingsForProject();
            Check(BuildExtendedSettings.Parent == BuildExtendedSettingsHost && StcDownloadTab.Visibility == Visibility.Collapsed &&
                SdccMemorySettingsPanel.Visibility == Visibility.Collapsed,
                "non-STC project keeps its compiler debug settings and no STC page");
            await JsonStore.WriteAsync(Path.Combine(directory, "result.json"), new
            {
                passed = true,
                hardware = false,
                checks
            });
        }
        finally { System.Windows.Application.Current.Resources["WorkbenchFontSize"] = originalSize; }
    }

    private static async Task WriteStcBuildSettingsPreviewProjectAsync(string root, ProjectManifest project)
    {
        // 仅验证保存/回读，不生成固件；最小离线清单只提供型号与 Flash 容量契约。
        var model = project.TemplateId == "rt51" ? "--model-large" : "--model-small";
        var device = new DeviceDefinition(project.DeviceId, project.DeviceId, "mcs51", 0, 65536, 0, 2304,
            project.ToolsetId, project.ToolsetVersion, project.CompilerId, ["-mmcs51"], [], [], [], "", [model],
            [model, "--code-size", "63488", "--iram-size", "256", "--xram-loc", "1", "--xram-size", "2047"],
            [new(project.TemplateId, "离线设置预览", "不编译或连接硬件", "main.c")]);
        await JsonStore.WriteAsync(Path.Combine(root, ".studiox", "project.json"), project);
        await JsonStore.WriteAsync(Path.Combine(root, "device", "manifest.json"),
            new PackManifest(1, project.PackId, project.PackVersion, "离线设置预览", "STC", [device]));
    }

    private async Task CheckRt51MemoryModelPreviewAsync(string directory, string theme, Action<bool, string> check)
    {
        projectDirectory = Path.Combine(directory, "projects", "rt51-" + theme);
        currentProjectManifest = new(1, "Rt51MemorySettingsPreview", "stc.stc8", "0.1.9", "", "IAP15F2K61S2", "rt51",
            "stc.sdcc", "1.0.0", "sdcc-4.5.0-15242");
        ConfigureBuildSettingsForProject();
        stcRamLimits = new(256, 1, 2047);
        var saved = new ProjectBuildSettings();
        ApplyBuildSettings(saved);
        await WriteStcBuildSettingsPreviewProjectAsync(projectDirectory, currentProjectManifest);
        var settingsPath = Path.Combine(projectDirectory, ProjectBuildSettings.RelativePath);
        await JsonStore.WriteAsync(settingsPath, saved);
        var before = await File.ReadAllBytesAsync(settingsPath);
        foreach (var model in new[] { SdccMemoryModel.Small, SdccMemoryModel.Medium })
        {
            SdccMemoryModelPicker.SelectedValue = model;
            check(!TrySelectedBuildSettings(out _, out var error) && error.Contains("RT51") &&
                !SaveBuildSettingsButton.IsEnabled && SdccMemoryModelPicker.SelectedValue is SdccMemoryModel selected && selected == model,
                "RT51 / " + theme + ": incompatible " + model + " remains visible with inline validation");
            SaveBuildSettings_Click(SaveBuildSettingsButton, new RoutedEventArgs());
            await pendingOperation;
            var after = await File.ReadAllBytesAsync(settingsPath);
            check(before.AsSpan().SequenceEqual(after) && loadedBuildSettings == saved,
                "RT51 / " + theme + ": incompatible " + model + " does not rewrite persisted settings");
        }
        SdccMemoryModelPicker.SelectedValue = SdccMemoryModel.Large;
        SaveBuildSettings_Click(SaveBuildSettingsButton, new RoutedEventArgs());
        await pendingOperation;
        check((await services.Builds.LoadSettingsAsync(projectDirectory)).MemoryModel == SdccMemoryModel.Large,
            "RT51 / " + theme + ": required large model saves successfully");
    }
}
