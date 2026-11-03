namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Engine;
using StudioX.Engine.StcUsb;

public partial class MainWindow
{
    private readonly Dictionary<string, ComboBox> stcHardwarePickers = new();
    private bool IsStcProject => IsStcSdccProject || IsMx251Project;

    private void ConfigureStcDownloadPage()
    {
        if (StcDownloadTab is null)
        {
            return;
        }
        StcDownloadTab.Visibility = Visibility.Collapsed;
        StcDownloadTarget.Text = currentProjectManifest?.DeviceId ?? "";
        StcDownloadPageStatus.Text = "";
        MoveSettingsSection(BuildExtendedSettings, IsStcProject ? StcBuildExtrasHost : BuildExtendedSettingsHost);
        MoveSettingsSection(Mx251ModelPanel, IsMx251Project && ProjectCapabilities.For(currentProjectManifest).Download ? StcBuildExtrasHost : BuildSettingsEditor);
        StcBuildExtrasHelp.Text = IsMx251Project
            ? "内存模型仍由 mx251.json 管理。当前 MX251 使用组件内置优化策略；这里不生成不存在的优化开关。"
            : "调试信息、Mon51 资源预留与 Flash 上限仍属于编译参数，保存后需重新编译。编译器优化、内存模型与 RAM 大小在“器件与模板”中设置。";
    }

    private static void MoveSettingsSection(FrameworkElement element, Panel destination)
    {
        if (element.Parent == destination)
        {
            return;
        }
        if (element.Parent is Panel previous)
        {
            previous.Children.Remove(element);
        }
        destination.Children.Add(element);
    }

    private async Task ShowStcDownloadSettingsAsync(string root, CancellationToken token)
    {
        if (IsStcSdccProject && loadedBuildSettings is null)
        {
            var settings = await services.Builds.LoadSettingsAsync(root, token);
            var limit = await services.Builds.ReadStcCodeRomLimitAsync(root, token);
            var ramLimits = await ReadStcRamLimitsForUiAsync(root, token);
            if (root != projectDirectory || closing)
            {
                return;
            }
            stcCodeRomLimit = limit;
            stcRamLimits = ramLimits;
            ApplyBuildSettings(settings);
        }
        if (IsMx251Project && loadedMx251Settings is null)
        {
            var settings = await services.Mx251Settings.ReadAsync(root, token);
            if (root != projectDirectory || closing)
            {
                return;
            }
            ApplyMx251Settings(settings);
        }
        if (IsStcSdccProject && loadedStcIspSettings is null)
        {
            await LoadStcIspProjectAsync(root, projectDetailsRevision);
        }
        if (IsMx251Project && ProjectCapabilities.For(currentProjectManifest).Download && loadedMx251Isp is null)
        {
            await LoadMx251IspAsync(root, projectDetailsRevision);
        }
        token.ThrowIfCancellationRequested();
        if (root == projectDirectory && !closing)
        {
            FocusStcSettings();
        }
    }

    private void ApplyStcHardwareSettings(StcIspSettings settings)
    {
        stcHardwarePickers.Clear();
        StcHardwareFields.Children.Clear();
        var fields = StcHardwareCatalog.For(currentProjectManifest?.DeviceId ?? "");
        var values = settings.Hardware?.Values();
        StcModifyHardware.IsChecked = settings.ModifyHardware;
        foreach (var field in fields)
        {
            var label = new TextBlock { Text = field.Label, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 4) };
            var picker = new ComboBox
            {
                ItemsSource = new[] { new StcHardwareValue("", "保留芯片当前值") }.Concat(field.Values).ToArray(),
                DisplayMemberPath = nameof(StcHardwareValue.Label),
                SelectedValuePath = nameof(StcHardwareValue.Value),
                MinHeight = 32
            };
            System.Windows.Automation.AutomationProperties.SetName(picker, field.Label);
            picker.SelectedValue = values?.GetValueOrDefault(field.Id) ?? "";
            picker.SelectionChanged += StcHardware_Changed;
            stcHardwarePickers.Add(field.Id, picker);
            StcHardwareFields.Children.Add(label);
            StcHardwareFields.Children.Add(picker);
        }
        UpdateStcHardwareControls();
    }

    private StcHardwareOptions? SelectedStcHardware()
    {
        var values = stcHardwarePickers.Where(p => p.Value.SelectedValue is string { Length: > 0 })
            .ToDictionary(p => p.Key, p => (string)p.Value.SelectedValue);
        return values.Count == 0 ? null : StcHardwareOptions.FromValues(values);
    }

    private void StcHardware_Changed(object sender, RoutedEventArgs e)
    {
        if (applyingStcIsp || StcHardwareFields is null)
        {
            return;
        }
        UpdateStcHardwareControls();
        UpdateStcIspDirtyStatus();
    }

    private void UpdateStcHardwareControls()
    {
        if (StcHardwareCard is null)
        {
            return;
        }
        var usb = StcDownloadModePicker.SelectedValue is StcDownloadMode.NativeUsb;
        var writable = IsStcSdccProject && stcHardwarePickers.Count > 0 && !projectActionsBusy && !services.Debugger.IsActive;
        StcModifyHardware.IsEnabled = writable;
        StcHardwareFields.Visibility = stcHardwarePickers.Count > 0 && IsStcSdccProject ? Visibility.Visible : Visibility.Collapsed;
        StcModifyHardware.Visibility = IsStcSdccProject ? Visibility.Visible : Visibility.Collapsed;
        StcHardwareFields.IsEnabled = writable && StcModifyHardware.IsChecked == true;
        foreach (var (id, picker) in stcHardwarePickers)
        {
            var supported = !usb || StcUsbHardwareOptions.Supports(currentProjectManifest?.DeviceId ?? "", id);
            // 未支持的串口草稿保留且仍可改为“保留”，避免切换到 USB 后无法修正旧选择。
            picker.IsEnabled = supported || picker.SelectedValue is string { Length: > 0 };
            picker.ToolTip = supported ? null : "此字段尚未接入原生 USB；已有选择仅作草稿，请设为保留或切换串口下载。";
        }
        SaveStcHardwareButton.IsEnabled = (IsStcSdccProject && loadedStcIspSettings is not null || IsMx251Project && loadedMx251Isp is not null) && !projectActionsBusy && !services.Debugger.IsActive;
        StcHardwareHelp.Text = IsMx251Project
            ? "此 32 位器件目前可设置 IRC 频率和固定运行模式。其它硬件选项暂不支持修改，下载时使用已核对的配置。"
            : usb ? "USB 可设置复位脚、振荡器增益、上电延时、低压检测、看门狗、下次 EEPROM 擦除及下载引脚条件。只有明确选择的字段会覆盖本次实机值；关闭修改开关时仅保留草稿。时钟当前支持保留 / RC 24 MHz。"
            : "关闭上方开关时，下方选择仅保存为草稿。启用后，只有明确选择的字段会覆盖本次握手读到的值；保存不会访问芯片。低压档位使用协议编号，电压依准确型号手册。Mon51 监控自动制作保留当前硬件选项。";
        StcHardwareUnavailable.Text = (usb ? "USB 暂未支持串口引脚重映射、UART2 模式、PWM 上电模式、复位输出电平及其它 RC 频率。" : "") +
            "暂不支持的 STC-ISP 选项：快速下载开关、RS485 等待时间、程序尾追加调节参数、此次 EEPROM 全部填 FF、Flash 空白区填充值及 EEPROM 分区。EEPROM 擦除开关表示芯片下次下载策略，不承诺此次 EEPROM 保留或填充。";
    }

    private async void SaveStcDownloadPage_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (IsMx251Project)
        {
            await SaveMx251IspSettingsAsync(token);
        }
        else
        {
            await SaveStcIspSettingsAsync(token);
        }
        StcDownloadPageStatus.Text = IsMx251Project ? Mx251IspStatus.Text : StcIspStatus.Text;
    });

    private void StcDownloadSection_Click(object sender, RoutedEventArgs e)
    {
        var target = (sender as Button)?.Tag as string;
        if (target == "hardware")
        {
            StcHardwareCard.BringIntoView();
        }
        else if (target == "debug")
        {
            StcBuildExtrasHost.BringIntoView();
        }
        else if (IsMx251Project)
        {
            Mx251IspPanel.BringIntoView();
        }
        else
        {
            StcIspPanel.BringIntoView();
        }
    }

    private void ResetStcHardware_Click(object sender, RoutedEventArgs e)
    {
        if (projectActionsBusy || services.Debugger.IsActive)
        {
            return;
        }
        foreach (var picker in stcHardwarePickers.Values)
        {
            picker.SelectedValue = "";
        }
        StcModifyHardware.IsChecked = false;
        UpdateStcIspDirtyStatus();
    }

    private void FocusStcBuildSettings()
    {
        if (loadedBuildSettings is { } saved &&
            (BuildOptimizationPicker.SelectedValue is CompilerOptimization selected && selected != saved.Optimization || HasSdccMemoryDraft(saved)))
        {
            PackagesTab.Visibility = Visibility.Visible;
            NewProjectPanel.Visibility = Visibility.Collapsed;
            ProjectDetailsPanel.Visibility = Visibility.Visible;
            ShowDocument(PackagesTab);
            if (BuildOptimizationPicker.SelectedValue is CompilerOptimization optimization && optimization != saved.Optimization)
            {
                BuildOptimizationPicker.BringIntoView();
            }
            else
            {
                SdccMemorySettingsPanel.BringIntoView();
            }
        }
        else
        {
            FocusStcSettings();
        }
    }
}
