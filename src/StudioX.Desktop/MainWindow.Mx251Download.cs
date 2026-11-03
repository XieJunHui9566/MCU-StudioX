namespace StudioX.Desktop;

using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using StudioX.Ai8051Isp;
using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    private Mx251IspSettings? loadedMx251Isp;
    private bool applyingMx251Isp;
    private void ConfigureMx251IspForProject()
    {
        loadedMx251Isp = null;
        applyingMx251Isp = true;
        Mx251DownloadModePicker.ItemsSource = new[] {
            new BuildChoice<Mx251DownloadMode>(Mx251DownloadMode.NativeUsb, "芯片原生 USB"),
            new BuildChoice<Mx251DownloadMode>(Mx251DownloadMode.Serial, "串口 ISP"),
            new BuildChoice<Mx251DownloadMode>(Mx251DownloadMode.UsbBridge, "底板 AI8H2K12G 下载桥") };
        Mx251DownloadModePicker.SelectedValue = Mx251DownloadMode.NativeUsb;
        Mx251SerialPortPicker.ItemsSource = null;
        Mx251SerialPortPicker.Text = "";
        applyingMx251Isp = false;
        UpdateMx251ConnectionMode();
        Mx251ClockFrequencyBox.Text = "40";
        Mx251IspPanel.Visibility = IsMx251Project && ProjectCapabilities.For(currentProjectManifest).Download ? Visibility.Visible : Visibility.Collapsed;
        Mx251DevicePicker.ItemsSource = null;
        Mx251IspStatus.Text = "";
        UpdateMx251IspControls();
    }
    private void UpdateMx251IspControls()
    {
        if (Mx251IspPanel is not null)
        {
            Mx251IspPanel.IsEnabled = IsMx251Project && projectDirectory is not null && !projectActionsBusy && !services.Debugger.IsActive;
        }
    }
    private async Task LoadMx251IspAsync(string root, int revision)
    {
        var settings = await services.Mx251Downloads.LoadSettingsAsync(root);
        var devices = await services.Mx251Downloads.ListDevicesAsync();
        var ports = await services.Mx251Downloads.ListSerialPortsAsync();
        if (closing || closed || root != projectDirectory || revision != projectDetailsRevision)
        {
            return;
        }
        Mx251SerialPortPicker.ItemsSource = ports;
        ApplyMx251Isp(settings, devices);
    }
    private void ApplyMx251Isp(Mx251IspSettings settings, IReadOnlyList<HidDevice> devices)
    {
        applyingMx251Isp = true;
        loadedMx251Isp = settings;
        Mx251DownloadModePicker.SelectedValue = settings.DownloadMode;
        Mx251SerialPortPicker.Text = settings.SerialPort;
        Mx251ClockFrequencyBox.Text = (settings.IrcHz / 1000000m).ToString("0.######", CultureInfo.InvariantCulture);
        Mx251DevicePicker.ItemsSource = devices.Select((d, i) => new BuildChoice<string>(d.Path, $"{d.Product} · USB 下载器 {i + 1}")).ToArray();
        Mx251DevicePicker.SelectedValue = settings.DevicePath.Length == 0 && devices.Count == 1 ? devices[0].Path : settings.DevicePath;
        Mx251IspStatus.Text = "";
        applyingMx251Isp = false;
        UpdateMx251ConnectionMode();
        UpdateMx251IspControls();
    }
    private Mx251IspSettings ReadMx251IspSettings()
    {
        var text = Mx251ClockFrequencyBox.Text.Trim();
        if ((!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var mhz) &&
             !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.CurrentCulture, out mhz)) ||
            mhz < 1 || mhz > 48 || mhz * 1000000m != decimal.Truncate(mhz * 1000000m))
        {
            throw new StudioXException("MX251_ISP_CLOCK", "IRC 频率请输入 1–48 MHz，最多 6 位小数，例如 11.0592、24 或 40。");
        }
        return new(Mx251DevicePicker.SelectedValue as string ?? loadedMx251Isp?.DevicePath ?? "", 32, (int)(mhz * 1000000m),
            Mx251DownloadModePicker.SelectedValue is Mx251DownloadMode mode ? mode : Mx251DownloadMode.NativeUsb, Mx251SerialPortPicker.Text.Trim().ToUpperInvariant());
    }
    private void UpdateMx251ConnectionMode()
    {
        if (Mx251BridgePanel is null || Mx251SerialPanel is null || Mx251ConnectionHelp is null) return;
        var mode = Mx251DownloadModePicker.SelectedValue is Mx251DownloadMode selected ? selected : Mx251DownloadMode.NativeUsb;
        Mx251BridgePanel.Visibility = mode == Mx251DownloadMode.UsbBridge ? Visibility.Visible : Visibility.Collapsed;
        Mx251SerialPanel.Visibility = mode == Mx251DownloadMode.Serial ? Visibility.Visible : Visibility.Collapsed;
        Mx251ConnectionHelp.Text = mode switch {
            Mx251DownloadMode.NativeUsb => "连接 AI8051U 芯片 USB，进入硬件 ISP 模式后自动发现，无需 COM 或固定 USB 插口。",
            Mx251DownloadMode.Serial => "连接芯片 UART 下载接口，选择对应 COM；由应用服务独占串口。",
            _ => "连接底板 USB1 的 AI8H2K12G 下载桥，可自动进入下载并校准 IRC。" };
    }
    private void Mx251DownloadMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateMx251ConnectionMode();
        Mx251DevicePicker_Changed(sender, e);
    }
    private void Mx251ClockFrequency_Changed(object sender, TextChangedEventArgs e)
    {
        if (!applyingMx251Isp && loadedMx251Isp is not null && Mx251IspStatus is not null)
        {
            Mx251IspStatus.Text = "未保存";
        }
    }
    private void Mx251DevicePicker_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!applyingMx251Isp && loadedMx251Isp is not null && Mx251IspStatus is not null)
        {
            Mx251IspStatus.Text = "未保存";
        }
    }
    private async Task RefreshMx251DevicesAsync(string root, CancellationToken token)
    {
        var settings = ReadMx251IspSettings();
        var saveStatus = Mx251IspStatus.Text;
        var devices = await services.Mx251Downloads.ListDevicesAsync(token);
        var ports = await services.Mx251Downloads.ListSerialPortsAsync(token);
        if (closing || closed || projectDirectory != root)
        {
            return;
        }
        Mx251SerialPortPicker.ItemsSource = ports;
        ApplyMx251Isp(settings, devices);
        Mx251IspStatus.Text = saveStatus;
    }
    private async void RefreshMx251Devices_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
        await RefreshMx251DevicesAsync(RequireProject(), token));
    private async void SaveMx251Isp_Click(object sender, RoutedEventArgs e) => await RunAsync(SaveMx251IspSettingsAsync);
    private async Task SaveMx251IspSettingsAsync(CancellationToken token)
    {
        try
        {
            var settings = ReadMx251IspSettings();
            await SaveMx251ClockAndDeviceAsync(RequireProject(), settings, token);
            loadedMx251Isp = settings;
            Mx251IspStatus.Text = Status.Text = "保存成功";
        }
        catch
        {
            Mx251IspStatus.Text = "保存失败";
            throw;
        }
    }
    private async Task<bool> ResolveMx251DownloadDeviceAsync(string root, CancellationToken token)
    {
        var draft = loadedMx251Isp is null ? await services.Mx251Downloads.LoadSettingsAsync(root, token) : ReadMx251IspSettings();
        if (draft.DownloadMode != Mx251DownloadMode.UsbBridge)
        {
            if (draft.DownloadMode == Mx251DownloadMode.Serial && string.IsNullOrWhiteSpace(draft.SerialPort))
            {
                var ports = await services.Mx251Downloads.ListSerialPortsAsync(token);
                if (ports.Length == 1) draft = draft with { SerialPort = ports[0] };
            }
            draft.Validate(requireDevice: true);
            await SaveMx251ClockAndDeviceAsync(root, draft, token);
            ApplyMx251Isp(draft, []);
            Mx251IspStatus.Text = "保存成功";
            return true;
        }
        var choice = await services.Mx251Downloads.ResolveDeviceAsync(root,
            loadedMx251Isp is null ? null : Mx251DevicePicker.SelectedValue as string, token);
        token.ThrowIfCancellationRequested();
        if (closing || closed || projectDirectory != root)
        {
            return false;
        }
        var selected = choice.Selected;
        if (selected is null)
        {
            var picker = new Mx251DownloadDeviceWindow(choice) { Owner = this };
            if (picker.ShowDialog() != true || picker.DevicePath is null)
            {
                Status.Text = "已取消下载。";
                return false;
            }
            selected = choice.SavedSettings with
            {
                DevicePath = picker.DevicePath
            };
        }
        if (draft is not null)
        {
            selected = selected with
            {
                IrcHz = draft.IrcHz, DownloadMode = draft.DownloadMode, SerialPort = draft.SerialPort
            };
        }
        await SaveMx251ClockAndDeviceAsync(root, selected, token);
        ApplyMx251Isp(selected, choice.Devices);
        Mx251IspStatus.Text = "保存成功";
        return true;
    }
    private async Task SaveMx251ClockAndDeviceAsync(string root, Mx251IspSettings settings, CancellationToken token)
    {
        if (editorDocuments.Any(d => d.IsDirty && d.Source.RelativePath == Mx251Profile.ConfigurationFile))
        {
            throw new StudioXException("MX251_SETTINGS_DIRTY", "请先保存 mx251.json 的编辑，再保存频率设置。");
        }
        var before = await services.Mx251Settings.ReadAsync(root, token);
        await services.Mx251Downloads.SaveSettingsAsync(root, settings, token);
        var saved = await services.Mx251Settings.ReadAsync(root, token);
        if (FindEditor(Mx251Profile.ConfigurationFile) is { IsDirty: false } editor)
        {
            EditorSynchronizer.Apply(editor, saved.Configuration);
        }
        // 时钟与内存模型共用配置；仅刷新本次保存造成的哈希变化，不覆盖既有外部编辑冲突。
        if (loadedMx251Settings?.Configuration.DiskHash == before.Configuration.DiskHash)
        {
            loadedMx251Settings = saved;
        }
    }
    private async Task DownloadMx251Async(string root, CancellationToken token)
    {
        if (!await ResolveMx251DownloadDeviceAsync(root, token))
        {
            return;
        }
        if (loadedMx251Settings is not null && Mx251ModelPicker.SelectedValue as string != loadedMx251Settings.Model)
        {
            await SaveMx251SettingsAsync(root, token);
        }
        await SaveAllSourcesAsync(root, token);
        ShowBottom(0);
        BuildLog.Clear();
        var build = await BuildWithSummaryAsync(root, token);
        if (!build.Success)
        {
            Status.Text = "编译失败，未启动下载。";
            return;
        }
        var prepared = await services.Mx251Downloads.PrepareAsync(root, token);
        token.ThrowIfCancellationRequested();
        if (closing || closed || projectDirectory != root)
        {
            return;
        }
        EnsureNoActiveDebug();
        if (editorDocuments.Any(d => d.IsDirty))
        {
            Status.Text = "编译期间有未保存编辑，请保存后重新下载。";
            return;
        }
        Log($"AI8051U-34K64 · 固定 32 位 · 内部 IRC 目标 {prepared.Settings.IrcHz / 1000000m:0.######} MHz\n固件：{prepared.SourceImage}\nSHA-256：{prepared.ImageSha256}\n下载日志：{prepared.LogPath}");
        Log("ISP 将覆盖当前程序，并可能擦除 EEPROM；请关闭官方 ISP 及该板串口监视器。具体连接与上电提示见下方输出。");
        BeginBuildOutput("等待 AI8051U 下载连接", "下载中");
        try
        {
            var report = await services.Mx251Downloads.DownloadPreparedAsync(prepared, new Progress<string>(line =>
            {
                using var json = JsonDocument.Parse(line);
                var entry = json.RootElement;
                if (entry.TryGetProperty("stage", out var stage) && stage.GetString() == "program")
                {
                    ReportBuildActivity($"写入固件 · {entry.GetProperty("acknowledgedBytes").GetInt32()} / {entry.GetProperty("totalBytes").GetInt32()} 字节 ACK");
                }
                else
                {
                    Log(line);
                }
            }), token);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Status.Text = report.Summary;
            Log(report.Summary);
            FinishBuildOutput(report.Summary, report.Success);
        }
        catch (Exception error)
        {
            Status.Text = error is OperationCanceledException ? "下载已取消；若已擦写，程序或配置可能不完整。" : "MX251 下载失败，原始诊断和阶段见下载日志。";
            FinishBuildOutput(Status.Text, false);
            throw;
        }
    }
}
