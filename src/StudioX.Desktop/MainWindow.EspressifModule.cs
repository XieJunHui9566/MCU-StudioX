namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using StudioX.Engine;

public partial class MainWindow
{
    private EspressifModuleSettings? loadedEspressifModule;
    private EspressifModuleCapabilities? espressifModuleCapabilities;
    private IReadOnlyList<EspressifModuleProfile> espressifModuleProfiles = [];
    private bool applyingEspressifModule;
    private sealed record EspressifModuleChoice(string Id, string Label, EspressifModuleProfile? Profile = null);

    private void InitializeEspressifModuleSettings() => ConfigureEspressifModuleForProject();

    private void ConfigureEspressifModuleForProject()
    {
        applyingEspressifModule = true;
        loadedEspressifModule = null;
        espressifModuleCapabilities = null;
        espressifModuleProfiles = [];
        EspressifModulePanel.Visibility = IsEspressifProject ? Visibility.Visible : Visibility.Collapsed;
        EspressifModuleProfilePicker.ItemsSource = null;
        foreach (var picker in new[] { EspressifFlashSizePicker, EspressifFlashModePicker, EspressifFlashFrequencyPicker,
            EspressifPsramModePicker, EspressifRevisionPicker, EspressifCorePicker })
        {
            picker.ItemsSource = null;
        }
        EspressifPsramSizeBox.Clear();
        EspressifModuleTarget.Text = currentProjectManifest?.Espressif is { } sdk ? $"芯片目标：{sdk.Target} · 模组规格独立配置" : "";
        EspressifModuleSummary.Text = "";
        EspressifModuleStatus.Text = IsEspressifProject ? "正在读取模组与存储配置…" : "";
        EspressifRevisionPanel.Visibility = EspressifCorePanel.Visibility = Visibility.Collapsed;
        applyingEspressifModule = false;
        UpdateEspressifModuleControls();
    }

    private async Task LoadEspressifModuleAsync(string directory, int revision)
    {
        var settingsTask = services.Builds.LoadEspressifModuleSettingsAsync(directory);
        var profilesTask = services.Builds.ListEspressifModuleProfilesAsync(directory);
        var capabilitiesTask = services.Builds.ReadEspressifModuleCapabilitiesAsync(directory);
        try
        {
            await Task.WhenAll(settingsTask, profilesTask, capabilitiesTask);
        }
        catch (Exception ex)
        {
            if (IsCurrentEspressifModuleProject(directory, revision))
            {
                EspressifModuleStatus.Text = "读取配置失败：" + ex.Message;
            }
            throw;
        }
        // 页面读取可以晚于工程切换；旧工程的配置不能回填到新工程。
        if (!IsCurrentEspressifModuleProject(directory, revision))
        {
            return;
        }
        espressifModuleProfiles = await profilesTask;
        espressifModuleCapabilities = await capabilitiesTask;
        applyingEspressifModule = true;
        EspressifModuleProfilePicker.ItemsSource = new[]
        {
            new EspressifModuleChoice("inherit", "沿用 sdkconfig（未指定模组）")
        }.Concat(espressifModuleProfiles.Select(profile => new EspressifModuleChoice(profile.Id, profile.Label, profile)))
            .Append(new("custom", "自定义 · 按板卡参数配置")).ToArray();
        var capabilities = espressifModuleCapabilities;
        EspressifFlashSizePicker.ItemsSource = new[] { new BuildChoice<int?>(null, "沿用") }
            .Concat(capabilities.FlashSizesMb.Select(size => new BuildChoice<int?>(size, $"{size} MiB"))).ToArray();
        EspressifFlashModePicker.ItemsSource = new[] { new BuildChoice<string?>(null, "沿用") }
            .Concat(capabilities.FlashModes.Select(mode => new BuildChoice<string?>(mode, mode.ToUpperInvariant()))).ToArray();
        EspressifFlashFrequencyPicker.ItemsSource = new[] { new BuildChoice<int?>(null, "沿用") }
            .Concat(capabilities.FlashFrequenciesMhz.Select(frequency => new BuildChoice<int?>(frequency, $"{frequency} MHz"))).ToArray();
        EspressifPsramModePicker.ItemsSource = new[] { new BuildChoice<string?>(null, "沿用") }
            .Concat(capabilities.PsramModes.Select(mode => new BuildChoice<string?>(mode, PsramLabel(mode)))).ToArray();
        var revisions = capabilities.P4RevisionFamilies ?? [];
        EspressifRevisionPanel.Visibility = revisions.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EspressifRevisionPicker.ItemsSource = new[] { new BuildChoice<string?>(null, "沿用 sdkconfig") }
            .Concat(revisions.Select(family => new BuildChoice<string?>(family,
                family == "legacy" ? "早期版本 · NRW16 / NRW32" : "新版 · NRW16X / NRW32X"))).ToArray();
        EspressifCorePanel.Visibility = capabilities.SupportsSingleCore ? Visibility.Visible : Visibility.Collapsed;
        EspressifCorePicker.ItemsSource = new[]
        {
            new BuildChoice<bool?>(null, "沿用 sdkconfig"), new(true, "单核"), new(false, "双核")
        };
        applyingEspressifModule = false;
        ApplyEspressifModuleSettings(await settingsTask);
    }

    private bool IsCurrentEspressifModuleProject(string directory, int revision) =>
        revision == projectDetailsRevision && !closing && IsEspressifProject &&
        string.Equals(projectDirectory, directory, StringComparison.OrdinalIgnoreCase);

    private static string PsramLabel(string mode) => mode switch
    {
        "disabled" => "关闭",
        "quad" => "Quad（4线）",
        "octal" => "Octal（8线）",
        "hex" => "HEX（16线）",
        _ => mode
    };

    private void ApplyEspressifModuleSettings(EspressifModuleSettings settings)
    {
        loadedEspressifModule = settings;
        applyingEspressifModule = true;
        EspressifModuleProfilePicker.SelectedValue = settings.ProfileId ?? (settings.HasOverrides ? "custom" : "inherit");
        ApplyEspressifModuleParameters(settings);
        applyingEspressifModule = false;
        EspressifModuleStatus.Text = "修改后点击保存，下次编译生效。";
        UpdateEspressifModuleControls();
    }

    private void ApplyEspressifModuleParameters(EspressifModuleSettings settings)
    {
        SelectModuleValue(EspressifFlashSizePicker, settings.FlashSizeMb);
        SelectModuleValue(EspressifFlashModePicker, settings.FlashMode);
        SelectModuleValue(EspressifFlashFrequencyPicker, settings.FlashFrequencyMhz);
        SelectModuleValue(EspressifPsramModePicker, settings.PsramMode);
        EspressifPsramSizeBox.Text = settings.PsramSizeMb?.ToString(CultureInfo.InvariantCulture) ?? "";
        SelectModuleValue(EspressifRevisionPicker, settings.P4RevisionFamily);
        SelectModuleValue(EspressifCorePicker, settings.SingleCore);
    }

    private static void SelectModuleValue<T>(ComboBox picker, T value) =>
        picker.SelectedItem = picker.Items.OfType<BuildChoice<T>>().FirstOrDefault(choice => EqualityComparer<T>.Default.Equals(choice.Value, value));

    private static T? ModuleValue<T>(ComboBox picker) => picker.SelectedItem is BuildChoice<T> choice ? choice.Value : default;

    private bool TrySelectedEspressifModule(out EspressifModuleSettings settings, out string error)
    {
        settings = new();
        error = "";
        if (EspressifModuleProfilePicker.SelectedItem is not EspressifModuleChoice choice)
        {
            return false;
        }
        if (choice.Profile is { } profile)
        {
            settings = profile.Settings;
            return true;
        }
        if (choice.Id == "inherit")
        {
            return true;
        }
        var psramMode = ModuleValue<string?>(EspressifPsramModePicker);
        int? psramSize = null;
        if (psramMode is not (null or "disabled") && EspressifPsramSizeBox.Text.Trim() is { Length: > 0 } text)
        {
            var allowedSizes = psramMode == "quad" ? new[] { 2, 4, 8 } : new[] { 4, 8, 16, 32, 64 };
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || !allowedSizes.Contains(value))
            {
                error = $"{PsramLabel(psramMode)} PSRAM 容量请输入 {string.Join("、", allowedSizes)} MiB；未知可留空。";
                return false;
            }
            psramSize = value;
        }
        settings = new(FlashSizeMb: ModuleValue<int?>(EspressifFlashSizePicker),
            FlashMode: ModuleValue<string?>(EspressifFlashModePicker), FlashFrequencyMhz: ModuleValue<int?>(EspressifFlashFrequencyPicker),
            PsramMode: psramMode, PsramSizeMb: psramSize, P4RevisionFamily: ModuleValue<string?>(EspressifRevisionPicker),
            SingleCore: espressifModuleCapabilities?.SupportsSingleCore == true ? ModuleValue<bool?>(EspressifCorePicker) : null);
        return true;
    }

    private void EspressifModuleProfile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (applyingEspressifModule || loadedEspressifModule is null)
        {
            return;
        }
        applyingEspressifModule = true;
        if (EspressifModuleProfilePicker.SelectedItem is EspressifModuleChoice { Profile: { } profile })
        {
            ApplyEspressifModuleParameters(profile.Settings);
        }
        else if (EspressifModuleProfilePicker.SelectedValue is "inherit")
        {
            ApplyEspressifModuleParameters(new());
        }
        applyingEspressifModule = false;
        UpdateEspressifModuleDirtyStatus();
    }

    private void EspressifModuleParameter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (applyingEspressifModule || loadedEspressifModule is null)
        {
            return;
        }
        if (ReferenceEquals(sender, EspressifPsramModePicker) && ModuleValue<string?>(EspressifPsramModePicker) is null or "disabled")
        {
            applyingEspressifModule = true;
            EspressifPsramSizeBox.Clear();
            applyingEspressifModule = false;
        }
        UpdateEspressifModuleDirtyStatus();
    }

    private void EspressifPsramSize_Changed(object sender, TextChangedEventArgs e)
    {
        if (!applyingEspressifModule && loadedEspressifModule is not null)
        {
            UpdateEspressifModuleDirtyStatus();
        }
    }

    private void UpdateEspressifModuleDirtyStatus()
    {
        UpdateEspressifModuleControls();
        EspressifModuleStatus.Text = !TrySelectedEspressifModule(out var settings, out var error) ? error
            : settings == loadedEspressifModule ? "配置与已保存内容一致。" : "模组配置尚未保存。";
    }

    private void UpdateEspressifModuleControls()
    {
        if (EspressifModuleEditor is null)
        {
            return;
        }
        var enabled = IsEspressifProject && projectDirectory is not null && loadedEspressifModule is not null &&
            espressifModuleCapabilities is not null && !projectActionsBusy && !services.Debugger.IsActive;
        EspressifModuleEditor.IsEnabled = enabled;
        var custom = EspressifModuleProfilePicker.SelectedValue is "custom";
        // 已核实预设不可局部修改，否则显示的模组型号会与实际参数不一致。
        EspressifModuleParameters.IsEnabled = custom;
        EspressifPsramSizeBox.IsEnabled = custom && ModuleValue<string?>(EspressifPsramModePicker) is not (null or "disabled");
        var valid = TrySelectedEspressifModule(out var selected, out _);
        SaveEspressifModuleButton.IsEnabled = enabled && valid && selected != loadedEspressifModule;
        CancelEspressifModuleButton.IsEnabled = enabled && (!valid || selected != loadedEspressifModule);
        EspressifModuleSummary.Text = EspressifModuleProfilePicker.SelectedItem is EspressifModuleChoice { Profile: { } profile }
            ? profile.Summary : custom ? "根据实际板卡或模组规格填写；可用选项来自当前芯片的内置 SDK。"
            : "保留现有 sdkconfig / sdkconfig.defaults，由工程决定存储与核心配置。";
        EspressifModuleSummary.ToolTip = (EspressifModuleProfilePicker.SelectedItem as EspressifModuleChoice)?.Profile?.SourceUrl;
    }

    private async void SaveEspressifModule_Click(object sender, RoutedEventArgs e)
    {
        if (projectActionsBusy || services.Debugger.IsActive || loadedEspressifModule is null ||
            !TrySelectedEspressifModule(out var settings, out _))
        {
            return;
        }
        var directory = RequireProject();
        var revision = projectDetailsRevision;
        await RunAsync(async token =>
        {
            EnsureNoActiveDebug();
            try
            {
                await services.Builds.SaveEspressifModuleSettingsAsync(directory, settings, token);
            }
            catch (Exception ex)
            {
                if (IsCurrentEspressifModuleProject(directory, revision))
                {
                    EspressifModuleStatus.Text = ex.Message;
                }
                throw;
            }
            if (!IsCurrentEspressifModuleProject(directory, revision))
            {
                return;
            }
            loadedEspressifModule = settings;
            BuildMemory.SetMessage("模组与存储配置已修改，重新编译后更新占用。");
            await RefreshEspressifModuleEditorAsync(directory, revision, token);
            if (IsCurrentEspressifModuleProject(directory, revision))
            {
                EspressifModuleStatus.Text = Status.Text = "模组配置已保存，下次编译生效。";
            }
        });
    }

    private async Task RefreshEspressifModuleEditorAsync(string directory, int revision, CancellationToken token)
    {
        if (FindEditor(EspressifModuleSettings.RelativePath) is { } session)
        {
            var disk = await services.Files.ReadAsync(directory, EspressifModuleSettings.RelativePath, token);
            if (!IsCurrentEspressifModuleProject(directory, revision))
            {
                return;
            }
            if (EditorSynchronizer.Apply(session, disk) == EditorDiskSyncResult.UnsavedChangesPreserved)
            {
                Log("模组设置已落盘；编辑器存在未保存修改，保留缓冲区并等待用户核对。");
            }
        }
        if (IsCurrentEspressifModuleProject(directory, revision))
        {
            await RefreshProjectTreeAsync(token: token);
        }
    }

    private void CancelEspressifModule_Click(object sender, RoutedEventArgs e)
    {
        if (loadedEspressifModule is not null && !projectActionsBusy && !services.Debugger.IsActive)
        {
            ApplyEspressifModuleSettings(loadedEspressifModule);
            EspressifModuleStatus.Text = "已撤销未保存的选择。";
        }
    }
}
