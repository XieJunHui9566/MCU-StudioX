namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    private ProjectBuildSettings? loadedBuildSettings;
    private StcCodeRomLimit? stcCodeRomLimit;
    private StcRamLimits? stcRamLimits;
    private bool applyingBuildSettings;
    private sealed record BuildChoice<T>(T Value, string Label);
    private bool IsStcSdccProject => currentProjectManifest is { ToolsetId: "stc.sdcc", CompilerId: "sdcc-4.5.0-15242" };

    private void InitializeBuildSettings()
    {
        BuildDebugInfoPicker.ItemsSource = new[]
        {
            new BuildChoice<CompilerDebugInfo>(CompilerDebugInfo.ProjectDefault, "沿用工程默认"),
            new(CompilerDebugInfo.None, "-g0 · 不生成调试信息"), new(CompilerDebugInfo.Standard, "-g2 · 标准调试信息"),
            new(CompilerDebugInfo.Full, "-g3 · 包含宏信息")
        };
        ConfigureBuildSettingsForProject();
    }

    private void ConfigureBuildSettingsForProject()
    {
        var stc = IsStcSdccProject;
        var mx = IsMx251Project;
        Mx251ModelPanel.Visibility = mx ? Visibility.Visible : Visibility.Collapsed;
        BuildOptimizationLabel.Visibility = BuildOptimizationPicker.Visibility = mx ? Visibility.Collapsed : Visibility.Visible;
        SdccMemorySettingsPanel.Visibility = stc ? Visibility.Visible : Visibility.Collapsed;
        var memoryChoices = new[]
        {
            new BuildChoice<SdccMemoryModel>(SdccMemoryModel.ProjectDefault, "沿用器件包默认"),
            new(SdccMemoryModel.Small, "small · 片内 DATA 默认数据"),
            new(SdccMemoryModel.Medium, "medium · 分页 PDATA 默认数据"),
            new(SdccMemoryModel.Large, "large · XDATA 默认数据")
        };
        var rt51 = stc && currentProjectManifest?.TemplateId == "rt51";
        SdccMemoryModelPicker.ItemsSource = memoryChoices;
        SdccMemoryModelHelp.Text = "small：DATA；medium：分页 PDATA，需按板级配置分页 / P2；large：XDATA。Mon51 暂不支持 PDATA 变量观察。改变默认数据区，不改变 MCU 位数，显式存储区声明不变。切换后重新编译。huge 需代码分区（bank）布局，暂不提供。" +
            (rt51 ? " RT51 的 ABI 要求 large 与自动栈；small / medium 无法保存。" : "");
        Mx251ModelPicker.ItemsSource = new[] { new BuildChoice<string>("small", "small · 小内存模型"), new BuildChoice<string>("large", "large · 大内存模型") };
        BuildDebugInfoPicker.ItemsSource = stc
            ? new[] { new BuildChoice<CompilerDebugInfo>(CompilerDebugInfo.ProjectDefault, "沿用工程默认"), new(CompilerDebugInfo.None, "不生成 CDB"), new(CompilerDebugInfo.Standard, "--debug · 生成源码调试 CDB") }
            : new[] { new BuildChoice<CompilerDebugInfo>(CompilerDebugInfo.ProjectDefault, "沿用工程默认"), new(CompilerDebugInfo.None, "-g0 · 不生成调试信息"), new(CompilerDebugInfo.Standard, "-g2 · 标准调试信息"), new(CompilerDebugInfo.Full, "-g3 · 包含宏信息") };
        BuildOptimizationPicker.ItemsSource = stc
            ? new[]
            {
                new BuildChoice<CompilerOptimization>(CompilerOptimization.ProjectDefault, "默认 · SDCC 默认策略"),
                new(CompilerOptimization.O0, "低优化 · 保留更直接的代码结构"),
                new(CompilerOptimization.Os, "代码尺寸 · 优先减小程序大小"),
                new(CompilerOptimization.O2, "执行速度 · 优先运行效率")
            }
            : new[]
            {
                new BuildChoice<CompilerOptimization>(CompilerOptimization.ProjectDefault, "沿用工程默认"),
                new(CompilerOptimization.O0, "-O0 · 不优化"), new(CompilerOptimization.Og, "-Og · 适合调试"),
                new(CompilerOptimization.O1, "-O1 · 基础优化"), new(CompilerOptimization.O2, "-O2 · 速度优化"),
                new(CompilerOptimization.O3, "-O3 · 更高速度优化"), new(CompilerOptimization.Os, "-Os · 优先减小体积")
            };
        BuildDebugInfoLabel.Visibility = BuildDebugInfoPicker.Visibility = mx ? Visibility.Collapsed : Visibility.Visible;
        StcCodeRomPanel.Visibility = stc ? Visibility.Visible : Visibility.Collapsed;
        StcMon51Profile.Visibility = stc && StcMon51Targets.Supports(currentProjectManifest?.DeviceId) ? Visibility.Visible : Visibility.Collapsed;
        BuildSettingsHelp.Text = mx ? "small / large 决定默认数据存储方式，不改变芯片的 32 位工作模式。保存后重新编译整个工程；源码中的显式存储区声明保持不变。" : stc
            ? "源码调试请选择 --debug，并建议低优化。构建生成 CDB；Mon51 连接会核对源码、构建产物与目标 CODE。优化可能使变量或部分源码行不可用。"
            : "源码调试建议 -Og 与 -g3；较高优化可能使变量或源码行无法直接观察。";
        if (stc) { BuildSettingsHelp.Text = "编译优化、内存模型与 RAM 大小按工程保存。调试配套、下载与硬件选项请使用工具栏下载按钮旁的设置入口。"; }
        ConfigureStcDownloadPage();
    }

    private ProjectBuildSettings SelectedBuildSettings => TrySelectedBuildSettings(out var settings, out var error)
        ? settings : throw new StudioXException("BUILD_SETTINGS", error);

    private bool TrySelectedBuildSettings(out ProjectBuildSettings settings, out string error)
    {
        settings = new(Optimization:
            BuildOptimizationPicker.SelectedValue is CompilerOptimization optimization ? optimization : CompilerOptimization.ProjectDefault,
            DebugInfo: BuildDebugInfoPicker.SelectedValue is CompilerDebugInfo debug ? debug : CompilerDebugInfo.ProjectDefault,
            Mon51Profile: IsStcSdccProject && StcMon51Profile.IsChecked == true,
            MemoryModel: IsStcSdccProject && SdccMemoryModelPicker.SelectedValue is SdccMemoryModel model
                ? model : SdccMemoryModel.ProjectDefault);
        error = "";
        if (!IsStcSdccProject || StcCodeRomSizeBox is null)
        {
            return true;
        }
        if (!TrySelectedRamSize(SdccIdataSizeBox.Text, "片内 RAM（IDATA）总量", out var idataBytes, out error) ||
            !TrySelectedRamSize(SdccXdataSizeBox.Text, "可用 XDATA 大小", out var xdataBytes, out error, allowZero: true))
        {
            return false;
        }
        settings = settings with { IdataSizeBytes = idataBytes, XdataSizeBytes = xdataBytes };
        if (stcRamLimits is null && (settings.MemoryModel != SdccMemoryModel.ProjectDefault || idataBytes is not null || xdataBytes is not null))
        {
            error = "尚未取得准确型号的 RAM 容量配置，无法保存内存覆盖项；请重新打开工程配置并检查器件包。";
            return false;
        }
        try
        {
            stcRamLimits?.Validate(settings);
        }
        catch (StudioXException ex) { error = ex.Message; return false; }
        var text = StcCodeRomSizeBox.Text.Trim();
        if (text.Length == 0)
        {
            return ValidateSelectedBuildSettings(settings, out error);
        }
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
        {
            error = "程序 Flash 容量上限请输入正整数字节数，或留空使用器件包默认值。";
            return false;
        }
        try
        {
            stcCodeRomLimit?.Validate(bytes);
        }
        catch (StudioXException ex) { error = ex.Message; return false; }
        settings = settings with
        {
            CodeRomSizeBytes = bytes
        };
        return ValidateSelectedBuildSettings(settings, out error);
    }

    private bool ValidateSelectedBuildSettings(ProjectBuildSettings settings, out string error)
    {
        try
        {
            if (currentProjectManifest is { } project) { settings.ValidateFor(project); }
            error = "";
            return true;
        }
        catch (StudioXException ex) { error = ex.Message; return false; }
    }

    private static bool TrySelectedRamSize(string text, string label, out int? bytes, out string error, bool allowZero = false)
    {
        bytes = null;
        error = "";
        if (string.IsNullOrWhiteSpace(text)) { return true; }
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < (allowZero ? 0 : 1))
        {
            error = label + (allowZero ? "请输入非负整数字节数" : "请输入正整数字节数") + "，或留空使用器件包默认值。";
            return false;
        }
        bytes = parsed;
        return true;
    }

    private async Task<StcRamLimits?> ReadStcRamLimitsForUiAsync(string root, CancellationToken token = default)
    {
        try { return await services.Builds.ReadStcRamLimitsAsync(root, token); }
        catch (Exception ex) when (ex is StudioXException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Log("读取 STC RAM 配置失败，内存覆盖项保持不可保存：" + ex);
            return null;
        }
    }

    private void ApplyBuildSettings(ProjectBuildSettings settings)
    {
        applyingBuildSettings = true;
        loadedBuildSettings = settings;
        var unsupportedOptimization = IsStcSdccProject && settings.Optimization is not
            (CompilerOptimization.ProjectDefault or CompilerOptimization.O0 or CompilerOptimization.O2 or CompilerOptimization.Os);
        var unsupportedMemoryModel = IsStcSdccProject && currentProjectManifest?.TemplateId == "rt51" &&
            settings.MemoryModel is SdccMemoryModel.Small or SdccMemoryModel.Medium;
        BuildOptimizationPicker.SelectedValue = unsupportedOptimization ? CompilerOptimization.ProjectDefault : settings.Optimization;
        BuildDebugInfoPicker.SelectedValue = IsStcSdccProject && settings.DebugInfo == CompilerDebugInfo.Full ? CompilerDebugInfo.ProjectDefault : settings.DebugInfo;
        StcCodeRomSizeBox.Text = IsStcSdccProject ? settings.CodeRomSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "" : "";
        StcMon51Profile.IsChecked = IsStcSdccProject && settings.Mon51Profile;
        SdccMemoryModelPicker.SelectedValue = IsStcSdccProject ? settings.MemoryModel : SdccMemoryModel.ProjectDefault;
        SdccIdataSizeBox.Text = IsStcSdccProject ? settings.IdataSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "" : "";
        SdccXdataSizeBox.Text = IsStcSdccProject ? settings.XdataSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "" : "";
        UpdateSdccRamLimitsText();
        StcCodeRomLimitText.Text = stcCodeRomLimit is { } limit
            ? $"留空使用器件包上限 {limit.MaximumBytes:N0} B（物理 {limit.PhysicalBytes:N0} B，保留 {limit.ReservedBytes} B）。"
            : "";
        applyingBuildSettings = false;
        BuildSettingsStatus.Text = unsupportedMemoryModel
            ? "已有内存模型不适用于 RT51；请选择沿用器件包默认或 large，并保存修正。"
            : unsupportedOptimization || IsStcSdccProject && settings.DebugInfo == CompilerDebugInfo.Full
            ? "已有参数不适用于 STC SDCC；请选择优化方式并保存，以移除旧设置。"
            : "修改后点击保存，下次编译生效。";
        UpdateBuildSettingsControls();
    }

    private void UpdateSdccRamLimitsText()
    {
        SdccIdataLimitText.Text = stcRamLimits is { } ram
            ? $"留空使用器件包默认上限 {ram.IdataSizeBytes:N0} B；DATA、IDATA 与硬件栈共用片内 RAM。"
            : IsStcSdccProject ? "尚未取得准确型号的片内 RAM 上限，请重新打开工程配置。" : "";
        SdccXdataLimitText.Text = stcRamLimits is { } xram
            ? $"留空使用{(StcMon51Profile.IsChecked == true ? "Mon51 可用" : "器件包默认")}上限 {xram.MaximumXdataBytes(StcMon51Profile.IsChecked == true):N0} B，起始地址 0x{xram.XdataOrigin:X4}；填 0 表示不提供 XDATA 空间。"
            : IsStcSdccProject ? "尚未取得准确型号的 XDATA 上限，请重新打开工程配置。" : "";
    }

    private void BuildSettings_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BuildSettingsSummary is null)
        {
            return;
        }
        UpdateBuildSettingsControls();
        UpdateBuildSettingsDirtyStatus();
    }
    private void StcMon51Profile_Changed(object sender, RoutedEventArgs e)
    {
        if (applyingBuildSettings || BuildSettingsSummary is null)
        {
            return;
        }
        if (StcMon51Profile.IsChecked == true)
        {
            BuildDebugInfoPicker.SelectedValue = CompilerDebugInfo.Standard;
        }
        UpdateSdccRamLimitsText();
        UpdateBuildSettingsControls();
        UpdateBuildSettingsDirtyStatus();
    }

    private void StcCodeRomSize_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (BuildSettingsSummary is null)
        {
            return;
        }
        UpdateBuildSettingsControls();
        UpdateBuildSettingsDirtyStatus();
    }

    private void UpdateBuildSettingsDirtyStatus()
    {
        if (IsMx251Project)
        {
            if (!applyingBuildSettings && loadedMx251Settings is not null)
            {
                BuildSettingsStatus.Text = Mx251ModelPicker.SelectedValue as string == loadedMx251Settings.Model ? "参数与已保存配置一致。" : "参数尚未保存。";
            }
            return;
        }
        if (applyingBuildSettings || loadedBuildSettings is null)
        {
            return;
        }
        BuildSettingsStatus.Text = !TrySelectedBuildSettings(out var settings, out var error) ? error
            : settings == loadedBuildSettings ? "参数与已保存配置一致。" : "参数尚未保存。";
    }

    private void UpdateBuildSettingsControls()
    {
        if (BuildSettingsEditor is null)
        {
            return;
        }
        if (IsMx251Project)
        {
            UpdateMx251SettingsControls();
            return;
        }
        var enabled = projectDirectory is not null && ProjectCapabilities.For(currentProjectManifest).BuildSettings && loadedBuildSettings is not null && !projectActionsBusy && !services.Debugger.IsActive;
        BuildSettingsEditor.IsEnabled = enabled;
        StcBuildExtrasHost.IsEnabled = enabled;
        if (!TrySelectedBuildSettings(out var selected, out var error))
        {
            SaveBuildSettingsButton.IsEnabled = false;
            BuildSettingsSummary.Text = error;
            return;
        }
        SaveBuildSettingsButton.IsEnabled = enabled && selected != loadedBuildSettings;
        BuildSettingsSummary.Text = IsStcSdccProject ? (selected.Optimization switch
        {
            CompilerOptimization.O0 => "STC SDCC · 低优化",
            CompilerOptimization.Os => "STC SDCC · 代码尺寸",
            CompilerOptimization.O2 => "STC SDCC · 执行速度",
            _ => "STC SDCC · 默认"
        }) + " · " + (selected.MemoryModel switch
        {
            SdccMemoryModel.Small => "small / DATA",
            SdccMemoryModel.Medium => "medium / PDATA",
            SdccMemoryModel.Large => "large / XDATA",
            _ => "内存模型沿用器件包"
        }) + (selected.IdataSizeBytes is { } idata ? $" · IDATA {idata:N0} B" : "")
        + (selected.XdataSizeBytes is { } xdata ? $" · XDATA {xdata:N0} B" : "")
        + (selected.Mon51Profile ? " · Mon51：CODE ≤56317 B / XDATA ≤1024 B / CDB" : selected.CodeRomSizeBytes is { } bytes ? $" · ROM 上限 {bytes:N0} B" :
            stcCodeRomLimit is { } limit ? $" · ROM 上限 {limit.MaximumBytes:N0} B（器件包默认）" : "")
            : selected.Summary;
    }

    private bool HasSdccMemoryDraft(ProjectBuildSettings saved)
        => SdccMemoryModelPicker.SelectedValue is SdccMemoryModel model && model != saved.MemoryModel ||
            !TrySelectedRamSize(SdccIdataSizeBox.Text, "IDATA", out var idata, out _) || idata != saved.IdataSizeBytes ||
            !TrySelectedRamSize(SdccXdataSizeBox.Text, "XDATA", out var xdata, out _, allowZero: true) || xdata != saved.XdataSizeBytes;

    private async void SaveBuildSettings_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        EnsureNoActiveDebug();
        var directory = RequireProject();
        if (IsMx251Project)
        {
            await SaveMx251SettingsAsync(directory, token);
            return;
        }
        if (!TrySelectedBuildSettings(out var settings, out var error))
        {
            BuildSettingsStatus.Text = error;
            return;
        }
        await services.Builds.SaveSettingsAsync(directory, settings, token);
        loadedBuildSettings = settings;
        BuildMemory.SetMessage("编译参数已修改，重新编译后更新占用。");
        BuildSettingsStatus.Text = Status.Text = "编译参数已保存，下次编译生效。";
    });

    private void ResetBuildSettings_Click(object sender, RoutedEventArgs e)
    {
        if (IsMx251Project)
        {
            Mx251ModelPicker.SelectedValue = loadedMx251Settings?.DefaultModel;
            return;
        }
        BuildOptimizationPicker.SelectedValue = CompilerOptimization.ProjectDefault;
        BuildDebugInfoPicker.SelectedValue = CompilerDebugInfo.ProjectDefault;
        SdccMemoryModelPicker.SelectedValue = SdccMemoryModel.ProjectDefault;
        SdccIdataSizeBox.Clear();
        SdccXdataSizeBox.Clear();
        StcCodeRomSizeBox.Clear();
    }
}
