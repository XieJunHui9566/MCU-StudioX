namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Globalization;
using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    private ProjectBuildSettings? loadedBuildSettings;
    private StcCodeRomLimit? stcCodeRomLimit;
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
        BuildDebugInfoLabel.Visibility = BuildDebugInfoPicker.Visibility = stc ? Visibility.Collapsed : Visibility.Visible;
        StcCodeRomPanel.Visibility = stc ? Visibility.Visible : Visibility.Collapsed;
        BuildSettingsHelp.Text = stc
            ? "SDCC 的优化选项不等同于 GCC 的 -O 等级；选择后由 IDE 转换为对应的 SDCC 参数。当前 STC 工程暂不提供源码调试。"
            : "源码调试建议 -Og 与 -g3；较高优化可能使变量或源码行无法直接观察。";
    }

    private ProjectBuildSettings SelectedBuildSettings => TrySelectedBuildSettings(out var settings, out var error)
        ? settings : throw new StudioXException("BUILD_SETTINGS", error);

    private bool TrySelectedBuildSettings(out ProjectBuildSettings settings, out string error)
    {
        settings = new(Optimization:
            BuildOptimizationPicker.SelectedValue is CompilerOptimization optimization ? optimization : CompilerOptimization.ProjectDefault,
            DebugInfo: !IsStcSdccProject && BuildDebugInfoPicker.SelectedValue is CompilerDebugInfo debug ? debug : CompilerDebugInfo.ProjectDefault);
        error = "";
        if (!IsStcSdccProject || StcCodeRomSizeBox is null) return true;
        var text = StcCodeRomSizeBox.Text.Trim();
        if (text.Length == 0) return true;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
        {
            error = "程序 Flash 容量上限请输入正整数字节数，或留空使用器件包默认值。";
            return false;
        }
        try { stcCodeRomLimit?.Validate(bytes); }
        catch (StudioXException ex) { error = ex.Message; return false; }
        settings = settings with { CodeRomSizeBytes = bytes };
        return true;
    }

    private void ApplyBuildSettings(ProjectBuildSettings settings)
    {
        applyingBuildSettings = true;
        loadedBuildSettings = settings;
        var unsupportedOptimization = IsStcSdccProject && settings.Optimization is not
            (CompilerOptimization.ProjectDefault or CompilerOptimization.O0 or CompilerOptimization.O2 or CompilerOptimization.Os);
        BuildOptimizationPicker.SelectedValue = unsupportedOptimization ? CompilerOptimization.ProjectDefault : settings.Optimization;
        BuildDebugInfoPicker.SelectedValue = IsStcSdccProject ? CompilerDebugInfo.ProjectDefault : settings.DebugInfo;
        StcCodeRomSizeBox.Text = IsStcSdccProject ? settings.CodeRomSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "" : "";
        StcCodeRomLimitText.Text = stcCodeRomLimit is { } limit
            ? $"留空使用器件包上限 {limit.MaximumBytes:N0} B（物理 {limit.PhysicalBytes:N0} B，保留 {limit.ReservedBytes} B）。"
            : "";
        applyingBuildSettings = false;
        BuildSettingsStatus.Text = unsupportedOptimization || IsStcSdccProject && settings.DebugInfo != CompilerDebugInfo.ProjectDefault
            ? "已有参数不适用于 STC SDCC；请选择优化方式并保存，以移除旧设置。"
            : "修改后点击保存，下次编译生效。";
        UpdateBuildSettingsControls();
    }

    private void BuildSettings_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BuildSettingsSummary is null) return;
        UpdateBuildSettingsControls();
        UpdateBuildSettingsDirtyStatus();
    }

    private void StcCodeRomSize_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (BuildSettingsSummary is null) return;
        UpdateBuildSettingsControls();
        UpdateBuildSettingsDirtyStatus();
    }

    private void UpdateBuildSettingsDirtyStatus()
    {
        if (applyingBuildSettings || loadedBuildSettings is null) return;
        BuildSettingsStatus.Text = !TrySelectedBuildSettings(out var settings, out var error) ? error
            : settings == loadedBuildSettings ? "参数与已保存配置一致。" : "参数尚未保存。";
    }

    private void UpdateBuildSettingsControls()
    {
        if (BuildSettingsEditor is null) return;
        var enabled = projectDirectory is not null && loadedBuildSettings is not null && !projectActionsBusy && !services.Debugger.IsActive;
        BuildSettingsEditor.IsEnabled = enabled;
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
        }) + (selected.CodeRomSizeBytes is { } bytes ? $" · ROM 上限 {bytes:N0} B" :
            stcCodeRomLimit is { } limit ? $" · ROM 上限 {limit.MaximumBytes:N0} B（器件包默认）" : "")
            : selected.Summary;
    }

    private async void SaveBuildSettings_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        EnsureNoActiveDebug();
        var directory = RequireProject();
        if (!TrySelectedBuildSettings(out var settings, out var error))
        { BuildSettingsStatus.Text = error; return; }
        await services.Builds.SaveSettingsAsync(directory, settings, token);
        loadedBuildSettings = settings;
        BuildMemory.SetMessage("编译参数已修改，重新编译后更新占用。");
        BuildSettingsStatus.Text = Status.Text = "编译参数已保存，下次编译生效。";
    });

    private void ResetBuildSettings_Click(object sender, RoutedEventArgs e)
    {
        BuildOptimizationPicker.SelectedValue = CompilerOptimization.ProjectDefault;
        BuildDebugInfoPicker.SelectedValue = CompilerDebugInfo.ProjectDefault;
        StcCodeRomSizeBox.Clear();
    }
}
