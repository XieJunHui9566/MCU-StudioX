namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StudioX.Application.Serial;
using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    private StcIspSettings? loadedStcIspSettings;
    private StcIspCapabilities? stcIspCapabilities;
    private bool applyingStcIsp;

    private void InitializeStcIsp()
    {
        StcBaudPicker.ItemsSource = new[] { 19200, 38400, 57600, 115200 };
        StcBaudPicker.SelectedItem = 115200;
        ConfigureStcIspForProject();
    }

    private void ConfigureStcIspForProject()
    {
        if (StcIspPanel is null) return;
        loadedStcIspSettings = null;
        stcIspCapabilities = null;
        StcIspPanel.Visibility = IsStcSdccProject ? Visibility.Visible : Visibility.Collapsed;
        StcIspStatus.Text = IsStcSdccProject ? "正在读取 STC 下载设置…" : "";
        StcToolStatus.Text = "";
        UpdateStcIspControls();
    }

    private async Task LoadStcIspProjectAsync(string root, int revision)
    {
        try
        {
            var settingsTask = services.StcIsp.LoadSettingsAsync(root);
            var capabilitiesTask = services.StcIsp.GetCapabilitiesAsync(root);
            await Task.WhenAll(settingsTask, capabilitiesTask);
            var toolTask = ReadStcToolStatusSafeAsync();
            var portsTask = ReadStcPortsSafeAsync();
            await Task.WhenAll(toolTask, portsTask);
            if (revision != projectDetailsRevision || projectDirectory != root || closing) return;
            ApplyStcIspSettings(await settingsTask, await capabilitiesTask, await toolTask, await portsTask);
        }
        catch (Exception ex)
        {
            if (revision != projectDetailsRevision || projectDirectory != root || closing) return;
            StcIspStatus.Text = "读取 STC 下载设置失败：" + ex.Message;
            Log("STC 下载设置：" + ex);
        }
    }

    private async Task<StcIspToolStatus> ReadStcToolStatusSafeAsync()
    {
        try { return await services.StcIsp.GetToolStatusAsync(); }
        catch (Exception ex)
        {
            Log("读取 STC 下载工具状态失败：" + ex);
            return new(false, null, null, ex.Message + "；可重新选择 stcgal.exe。");
        }
    }

    private async Task<string[]> ReadStcPortsSafeAsync()
    {
        try { return await SerialTerminalService.ListPortsAsync(); }
        catch (Exception ex)
        {
            Log("列举 STC 串口失败：" + ex);
            return [];
        }
    }

    private void ApplyStcIspSettings(StcIspSettings settings, StcIspCapabilities capabilities, StcIspToolStatus tool, string[] ports)
    {
        applyingStcIsp = true;
        try
        {
            stcIspCapabilities = capabilities;
            loadedStcIspSettings = settings;
            StcClockModePicker.ItemsSource = capabilities.SupportedClockModes.Select(mode => new BuildChoice<StcClockMode>(mode, mode switch
            {
                StcClockMode.InternalRc => "内部 RC（由 ISP 配置）",
                StcClockMode.ExternalCrystal => "外部晶振（须在开发板上实装）",
                _ => "保留当前时钟来源（RC 可能重新校准）"
            })).ToArray();
            StcClockModePicker.SelectedValue = settings.ClockMode;
            StcClockFrequencyBox.Text = settings.ClockFrequencyHz is { } hz
                ? (hz / 1000000m).ToString("0.######", CultureInfo.InvariantCulture) : "";
            StcPortPicker.ItemsSource = ports;
            StcPortPicker.Text = settings.Port;
            StcBaudPicker.SelectedItem = settings.TransferBaud;
            if (StcBaudPicker.SelectedItem is null) StcBaudPicker.Text = settings.TransferBaud.ToString(CultureInfo.InvariantCulture);
            StcToolStatus.Text = (tool.Available ? "下载工具已就绪：" : "下载工具不可用：") + tool.Message +
                (tool.ProgrammerExecutable is null ? "" : "\n" + tool.ProgrammerExecutable);
            UpdateStcClockHelp();
            StcIspStatus.Text = settings.Port.Length == 0 ? "请选择 COM 串口并保存；下载前还会核对芯片型号与固件。" :
                "设置已加载。下载前将显示芯片型号、固件摘要和时钟选项供确认。";
        }
        finally { applyingStcIsp = false; }
        UpdateStcIspControls();
    }

    private void UpdateStcClockHelp()
    {
        if (StcClockModePicker is null || StcClockHelp is null) return;
        var mode = StcClockModePicker.SelectedValue is StcClockMode selected ? selected : StcClockMode.Preserve;
        var frequencyEnabled = mode == StcClockMode.ExternalCrystal ||
            mode == StcClockMode.InternalRc && stcIspCapabilities?.SupportsRcTrim == true;
        StcClockFrequencyLabel.Visibility = StcClockFrequencyBox.Visibility = frequencyEnabled ? Visibility.Visible : Visibility.Collapsed;
        StcClockFrequencyLabel.Text = mode == StcClockMode.ExternalCrystal ? "板上晶振频率（MHz）" : "内部 RC 目标频率（MHz）";
        StcClockHelp.Text = mode switch
        {
            StcClockMode.InternalRc when stcIspCapabilities?.SupportsRcTrim == true =>
                $"RC 可设置范围：{stcIspCapabilities.MinRcFrequencyHz / 1000000m:0.###}–{stcIspCapabilities.MaxRcFrequencyHz / 1000000m:0.###} MHz。留空时以芯片报告的当前频率为目标重新校准，实际频率可能略变。",
            StcClockMode.InternalRc => "该型号的串口 ISP 仅能切换到内部 RC，不能通过 stcgal 校准 RC 频率。",
            StcClockMode.ExternalCrystal => "填写开发板上实际焊接的 MCU 晶振频率；这个数值用于编译时钟参数。ISP 会切换到外部时钟源，也可能重写备用内部 RC 校准值。赛点 V3.1 的 IAP15 开发板没有 MCU 外部晶振。",
            _ => "保留芯片当前内/外部时钟来源。STC15/IAP15 下载时 stcgal 仍会重写内部 RC 校准参数，运行频率可能略变；使用外部时钟时也会重写备用 RC 校准值。本设置不覆盖工程代码中的时钟定义。"
        } + (stcIspCapabilities?.Warning is { Length: > 0 } warning ? "\n" + warning : "");
    }

    private bool TrySelectedStcIspSettings(out StcIspSettings settings, out string error)
    {
        settings = new();
        error = "";
        if (stcIspCapabilities is null) { error = "STC 型号能力尚未加载。"; return false; }
        var mode = StcClockModePicker.SelectedValue is StcClockMode selected ? selected : StcClockMode.Preserve;
        int? hz = null;
        if (mode == StcClockMode.ExternalCrystal || mode == StcClockMode.InternalRc && stcIspCapabilities.SupportsRcTrim)
        {
            var frequencyText = StcClockFrequencyBox.Text.Trim();
            if (frequencyText.Length > 0)
            {
                if ((!decimal.TryParse(frequencyText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var mhz) &&
                     !decimal.TryParse(frequencyText, NumberStyles.AllowDecimalPoint, CultureInfo.CurrentCulture, out mhz)) ||
                    mhz <= 0 || mhz > int.MaxValue / 1000000m || mhz * 1000000m != decimal.Truncate(mhz * 1000000m))
                { error = "时钟频率请输入 MHz 数值，最多 6 位小数（例如 11.0592）。"; return false; }
                hz = (int)(mhz * 1000000m);
            }
        }
        if (!int.TryParse(StcBaudPicker.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var baud))
        { error = "请选择有效的串口传输波特率。"; return false; }
        settings = new StcIspSettings(Port: StcPortPicker.Text.Trim().ToUpperInvariant(), ClockMode: mode,
            ClockFrequencyHz: hz, TransferBaud: baud);
        try { settings.ValidateFor(stcIspCapabilities); }
        catch (StudioXException ex) { error = ex.Message; return false; }
        return true;
    }

    private void UpdateStcIspControls()
    {
        if (StcIspPanel is null || applyingStcIsp) return;
        var enabled = IsStcSdccProject && projectDirectory is not null && loadedStcIspSettings is not null &&
            !projectActionsBusy && !services.Debugger.IsActive;
        StcIspPanel.IsEnabled = enabled;
        SaveStcIspSettingsButton.IsEnabled = enabled;
    }

    private void StcClockMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (StcClockHelp is null || applyingStcIsp) return;
        StcClockFrequencyBox.Clear();
        UpdateStcClockHelp();
        UpdateStcIspDirtyStatus();
    }

    private void StcClockFrequency_Changed(object sender, TextChangedEventArgs e) => UpdateStcIspDirtyStatus();

    private void StcIspSelection_Changed(object sender, RoutedEventArgs e) => UpdateStcIspDirtyStatus();

    private void UpdateStcIspDirtyStatus()
    {
        if (applyingStcIsp || loadedStcIspSettings is null || StcIspStatus is null) return;
        StcIspStatus.Text = !TrySelectedStcIspSettings(out var selected, out var error) ? error :
            selected == loadedStcIspSettings ? "设置与已保存配置一致。" : "设置尚未保存；保存后下次编译/下载生效。";
    }

    private async void RefreshStcPorts_Click(object sender, RoutedEventArgs e) => await RunAsync(async _ =>
    {
        var selected = StcPortPicker.Text;
        var ports = await SerialTerminalService.ListPortsAsync();
        StcPortPicker.ItemsSource = ports;
        StcPortPicker.Text = selected;
        StcIspStatus.Text = ports.Length == 0 ? "未发现 COM 串口；连接开发板后再刷新。" :
            "已刷新串口：" + string.Join("、", ports);
    });

    private async void SaveStcIspSettings_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        EnsureNoActiveDebug();
        if (!TrySelectedStcIspSettings(out var settings, out var error)) { StcIspStatus.Text = error; return; }
        await services.StcIsp.SaveSettingsAsync(RequireProject(), settings, token);
        loadedStcIspSettings = settings;
        StcIspStatus.Text = Status.Text = "STC 串口与时钟设置已保存；重新编译后生效，芯片配置将在下载时写入。";
    });

    private async void ChooseStcgal_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "stcgal 可执行文件|stcgal.exe", Title = "选择 stcgal 1.10" };
        if (dialog.ShowDialog(this) != true) return;
        await RunAsync(async token =>
        {
            await services.StcIsp.SaveProgrammerPathAsync(dialog.FileName, token);
            var tool = await services.StcIsp.GetToolStatusAsync(token);
            StcToolStatus.Text = "下载工具已就绪：" + tool.Message + "\n" + tool.ProgrammerExecutable;
            StcIspStatus.Text = Status.Text = "已保存本机 stcgal 工具位置。";
        });
    }

    private async Task DownloadStcAsync(string root, CancellationToken token)
    {
        if (loadedBuildSettings is not null && (!TrySelectedBuildSettings(out var selectedBuild, out var buildError) || selectedBuild != loadedBuildSettings))
        {
            FocusStcSettings();
            BuildSettingsStatus.Text = buildError.Length > 0 ? buildError : "编译参数尚未保存；请先保存后再下载。";
            return;
        }
        if (loadedStcIspSettings is not null && (!TrySelectedStcIspSettings(out var selectedIsp, out var ispError) || selectedIsp != loadedStcIspSettings))
        {
            FocusStcSettings();
            StcIspStatus.Text = ispError.Length > 0 ? ispError : "串口或时钟参数尚未保存；请先保存后再下载。";
            return;
        }
        var settings = await services.StcIsp.LoadSettingsAsync(root, token);
        var capabilities = await services.StcIsp.GetCapabilitiesAsync(root, token);
        try { settings.ValidateFor(capabilities, requirePort: true); }
        catch (StudioXException ex) { await ShowProjectDetailsAsync(); StcIspStatus.Text = ex.Message; return; }
        await SaveAllSourcesAsync(root, token);
        ShowBottom(0);
        BuildLog.Clear();
        var build = await BuildWithSummaryAsync(root, token);
        if (build.LogPath is { } logPath) Log("构建日志：" + logPath);
        Log(build.Summary);
        if (!build.Success) { Status.Text = "编译失败，未打开串口或下载。"; return; }
        var prepared = await services.StcIsp.PrepareAsync(root, settings, token);
        var clock = settings.ClockMode switch
        {
            StcClockMode.InternalRc => "内部 RC" + (settings.ClockFrequencyHz is { } hz ? $" · {hz / 1000000m:0.######} MHz" : " · 按当前值重新校准"),
            StcClockMode.ExternalCrystal => $"外部晶振 · {settings.ClockFrequencyHz / 1000000m:0.######} MHz（必须实装）",
            _ => "保留当前时钟来源；内部 RC 可能重新校准，频率略变"
        };
        var message = $"目标型号：{prepared.ExpectedModel}\n串口：{prepared.Port}\n固件：{prepared.SourceImage}\nSHA-256：{prepared.ImageSha256}\n时钟：{clock}\n\n串口 ISP 会擦除并覆盖芯片现有程序，且无法读回旧程序备份。下载开始等待后，请按开发板的下载/上电按钮。\n\n确认写入这份固件吗？";
        if (settings.ClockMode == StcClockMode.ExternalCrystal &&
            prepared.ExpectedModel.Equals("IAP15F2K61S2", StringComparison.OrdinalIgnoreCase))
            message += "\n\n注意：你先前使用的赛点 V3.1 开发板没有 MCU 外部晶振。如果仍是这块板，请取消下载并选择内部 RC。";
        if (MessageBox.Show(this, message, "确认 STC 串口下载", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        { Status.Text = "已取消 STC 下载，未打开串口。"; return; }
        Status.Text = "等待开发板进入 STC ISP…";
        StcIspReport result;
        try
        {
            result = await services.StcIsp.DownloadPreparedAsync(prepared,
                new Progress<string>(line => Log(line.TrimEnd('\r', '\n'))), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Status.Text = "STC 下载已取消；若已开始擦写，板内程序可能不完整。";
            Log(Status.Text);
            return;
        }
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        Log("下载日志：" + result.LogPath);
        Log(result.Summary);
        Status.Text = result.Summary;
    }

    private void FocusStcSettings()
    {
        PackagesTab.Visibility = Visibility.Visible;
        NewProjectPanel.Visibility = Visibility.Collapsed;
        ProjectDetailsPanel.Visibility = Visibility.Visible;
        ShowDocument(PackagesTab);
    }
}
