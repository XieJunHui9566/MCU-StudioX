namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using StudioX.Application.Lvgl;
using StudioX.Engine.Lvgl;
using StudioX.Foundation;

public partial class LvglPreviewView : UserControl
{
    private LvglPreviewService? service;
    private string? project;
    private LvglPreviewConfiguration? configuration;
    private CancellationTokenSource? cancellation;
    private CancellationTokenSource? projectLoadCancellation;
    private Task operation = Task.CompletedTask;
    private Task projectLoad = Task.CompletedTask;
    private int projectRevision;
    private bool busy, stopping, shutdown;
    private string uiDiagnostic = "";
    private string uiNotice = "";
    private readonly object snapshotSync = new();
    private LvglPreviewSnapshot? pendingSnapshot;
    private bool snapshotQueued;
    private bool setupBusy;
    public Func<string, CancellationToken, Task>? BeforeStartAsync
    {
        get; set;
    }
    public Action<string>? LogDiagnostic
    {
        get; set;
    }
    internal bool ConfigurationReady => configuration is not null;
    public Task WaitForProjectAsync() => projectLoad;

    public LvglPreviewView()
    {
        InitializeComponent();
        ZoomPicker.ItemsSource = new[] { 1, 2, 3, 4 };
        ZoomPicker.SelectedItem = 2;
        BufferCountPicker.ItemsSource = new[] { 1, 2 };
        BufferCountPicker.SelectedItem = 1;
        SetupView.Closed = () => { SetupView.Visibility = Visibility.Collapsed; UpdateButtons(); };
        SetupView.BusyChanged = active => { setupBusy = active; UpdateButtons(); };
        SetupView.LogDiagnostic = message => LogDiagnostic?.Invoke(message);
        SetupView.SavedAsync = AcceptSetupAsync;
        UpdateButtons();
    }

    public void Attach(LvglPreviewService source)
    {
        if (service is not null)
        {
            service.Changed -= PreviewChanged;
        }
        service = source;
        SetupView.Attach(source);
        source.Changed += PreviewChanged;
        RefreshToolchain();
    }

    public void SetProject(string? directory)
    {
        if (shutdown || string.Equals(project, directory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        projectLoadCancellation?.Cancel();
        projectLoadCancellation?.Dispose();
        projectLoadCancellation = new();
        project = directory;
        configuration = null;
        uiDiagnostic = uiNotice = "";
        SetupView.SetProject(directory, null);
        SetupView.Visibility = Visibility.Collapsed;
        var revision = ++projectRevision;
        ProjectLabel.Text = directory ?? "打开 LVGL 工程后，在独立窗口运行共享的界面代码。";
        StaleBanner.Visibility = ScreenshotSection.Visibility = Visibility.Collapsed;
        ScreenshotImage.Source = null;
        ScreenshotPath.Text = "";
        ResetStats();
        TargetMemory.SetMessage("打开 LVGL 工程后读取目标 ELF / MAP。");
        TargetCorrespondenceLabel.Text = "目标占用需核对所选 UI 与构建证据。";
        StateLabel.Text = directory is null ? "尚未打开工程" : "正在读取预览设置…";
        ConfigurationNotice.Text = "设置保存在工程 .studiox/lvgl-preview.json；外部编译链覆盖保存在本机设置中。";
        DiagnosticLog.Clear();
        UpdateButtons();
        projectLoad = LoadProjectAsync(directory, revision, projectLoadCancellation.Token);
    }

    private async Task LoadProjectAsync(string? directory, int revision, CancellationToken token)
    {
        if (directory is null || service is null)
        {
            return;
        }
        try
        {
            var config = await service.ReadConfigurationIfPresentAsync(directory, token);
            if (shutdown || revision != projectRevision)
            {
                return;
            }
            if (config is null)
            {
                StateLabel.Text = "尚未配置自定义 UI，点击「配置自定义 UI」选择共享源码。";
                ConfigurationNotice.Text = "向导会扫描 LVGL、共享 UI 和入口，并保存工程相对路径的预览配置。";
                TargetMemory.SetMessage("尚未配置当前预览 UI，目标资源占用未知。请先完成配置与目标构建。");
                TargetCorrespondenceLabel.Text = "尚未配置当前预览 UI，目标占用未知。";
                return;
            }
            configuration = config;
            SetupView.SetProject(directory, config);
            ApplyConfiguration(config);
            RenderSnapshot(service.GetSnapshot(directory));
            await ReadResourcesAsync(directory, revision, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!shutdown && revision == projectRevision)
            {
                StateLabel.Text = "当前工程还不能预览：" + ex.Message;
                ConfigurationNotice.Text = "可使用「配置自定义 UI」检查库、源码、入口和共享配置。";
                RecordDiagnostic(ex);
            }
        }
        finally { if (!shutdown && revision == projectRevision) { UpdateButtons(); } }
    }

    private void ApplyConfiguration(LvglPreviewConfiguration config)
    {
        WidthInput.Text = config.Width.ToString(CultureInfo.InvariantCulture);
        HeightInput.Text = config.Height.ToString(CultureInfo.InvariantCulture);
        RowsInput.Text = config.DrawBufferRows.ToString(CultureInfo.InvariantCulture);
        ZoomPicker.SelectedItem = config.Zoom;
        BufferCountPicker.SelectedItem = config.DrawBufferCount;
        AutoRebuildCheck.IsChecked = config.AutoRebuild;
        ColorDepthLabel.Text = config.ColorDepth + " bit";
        BuffersLabel.Text = $"LVGL 绘制缓存 {Size((long)config.Width * config.DrawBufferRows * (config.ColorDepth / 8) * config.DrawBufferCount)} · PC 显示帧缓存 {Size((long)config.Width * config.Height * 4)}";
    }

    private LvglPreviewConfiguration ReadConfiguration()
    {
        var current = configuration ?? throw new StudioXException("LVGL_CONFIG_REQUIRED", "请先配置工程的 LVGL 预览入口。");
        if (!int.TryParse(WidthInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(HeightInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var height) ||
            !int.TryParse(RowsInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var rows))
        {
            throw new StudioXException("LVGL_DISPLAY_SETTINGS", "宽度、高度、缓存行数需要填写正整数。");
        }
        return current with
        {
            Width = width,
            Height = height,
            DrawBufferRows = rows,
            Zoom = ZoomPicker.SelectedItem is int zoom ? zoom : current.Zoom,
            DrawBufferCount = BufferCountPicker.SelectedItem is int count ? count : current.DrawBufferCount,
            AutoRebuild = AutoRebuildCheck.IsChecked == true
        };
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
        => await StartPreviewAsync();

    public async Task StartPreviewAsync()
    {
        await projectLoad;
        if (service is null || project is not { } directory || busy || shutdown)
        {
            return;
        }
        operation = RunAsync(async token =>
        {
            await SaveConfigurationAsync(directory, token);
            if (BeforeStartAsync is not null)
            {
                await BeforeStartAsync(directory, token);
            }
            await service.StartAsync(directory, token);
            await ReadResourcesAsync(directory, projectRevision, token);
        });
        await operation;
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (service is null || stopping || shutdown)
        {
            return;
        }
        var directory = project;
        stopping = true;
        UpdateButtons();
        try
        {
            if (busy)
            {
                cancellation?.Cancel();
                await operation;
            }
            if (shutdown || directory is null || project != directory)
            {
                return;
            }
            operation = RunAsync(token => service.StopAsync(directory, token));
            await operation;
        }
        finally { stopping = false; if (!shutdown) { UpdateButtons(); } }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (project is not { } directory || busy || shutdown)
        {
            return;
        }
        operation = RunAsync(token => SaveConfigurationAsync(directory, token));
        await operation;
    }

    private async void Setup_Click(object sender, RoutedEventArgs e) => await OpenSetupAsync();

    public async Task OpenSetupAsync()
    {
        await projectLoad;
        if (service is null || project is null || busy || setupBusy || shutdown)
        {
            return;
        }
        if (SetupView.Visibility == Visibility.Visible)
        {
            await SetupView.CancelAsync();
            SetupView.Visibility = Visibility.Collapsed;
            UpdateButtons();
            return;
        }
        SetupView.SetProject(project, configuration);
        SetupView.Visibility = Visibility.Visible;
        UpdateButtons();
        await SetupView.InitializeExistingAsync();
    }

    private async Task AcceptSetupAsync(LvglPreviewConfiguration config, bool start, CancellationToken token)
    {
        if (service is null || project is not { } directory || shutdown)
        {
            return;
        }
        var revision = projectRevision;
        configuration = config;
        ApplyConfiguration(config);
        ConfigurationNotice.Text = "自定义 UI 配置已保存。";
        if (start)
        {
            if (BeforeStartAsync is not null)
            {
                await BeforeStartAsync(directory, token);
            }
            await service.StartAsync(directory, token);
        }
        await ReadResourcesAsync(directory, revision, token);
        if (!shutdown && revision == projectRevision)
        {
            RenderSnapshot(service.GetSnapshot(directory));
            SetupView.Visibility = Visibility.Collapsed;
        }
    }

    private async Task SaveConfigurationAsync(string directory, CancellationToken token)
    {
        if (service is null)
        {
            return;
        }
        var edited = ReadConfiguration();
        var latest = await service.ReadConfigurationAsync(directory, token);
        // 手工修改配置中的共享库或源码列表后，页面只能覆盖本页负责的显示设置。
        edited = latest with
        {
            Width = edited.Width,
            Height = edited.Height,
            DrawBufferRows = edited.DrawBufferRows,
            Zoom = edited.Zoom,
            DrawBufferCount = edited.DrawBufferCount,
            AutoRebuild = edited.AutoRebuild
        };
        if (edited != latest)
        {
            await service.SaveConfigurationAsync(directory, edited, token);
        }
        configuration = edited;
        ApplyConfiguration(edited);
        ConfigurationNotice.Text = "已保存；运行中的预览会按工程设置重建。";
    }

    private async void Toolchain_Click(object sender, RoutedEventArgs e)
    {
        if (service is null || busy || shutdown)
        {
            return;
        }
        var picker = new OpenFileDialog
        {
            Title = "选择外部 Windows PC GCC（不能选择 MCU 交叉编译器）",
            Filter = "GCC 编译器 (gcc.exe)|gcc.exe|可执行文件 (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (picker.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        operation = RunAsync(async token =>
        {
            await service.ConfigureToolchainAsync(picker.FileName, token);
            RefreshToolchain();
        });
        await operation;
    }

    private async void BundledToolchain_Click(object sender, RoutedEventArgs e)
    {
        if (service is null || busy || shutdown)
        {
            return;
        }
        operation = RunAsync(async token =>
        {
            await service.UseBundledToolchainAsync(token);
            RefreshToolchain();
        });
        await operation;
    }

    private async void Resources_Click(object sender, RoutedEventArgs e)
    {
        if (project is not { } directory || busy || shutdown)
        {
            return;
        }
        operation = RunAsync(async token =>
        {
            if (service is not null && configuration is null)
            {
                configuration = await service.ReadConfigurationAsync(directory, token);
                ApplyConfiguration(configuration);
            }
            await ReadResourcesAsync(directory, projectRevision, token);
        });
        await operation;
    }

    private async Task ReadResourcesAsync(string directory, int revision, CancellationToken token)
    {
        if (service is null)
        {
            return;
        }
        var report = await service.ReadResourcesAsync(directory, token);
        if (shutdown || revision != projectRevision)
        {
            return;
        }
        TargetMemory.SetReport(report.TargetBuild);
        TargetCorrespondenceLabel.Text = report.TargetEvidence?.Message ?? report.TargetBuild.Message;
        TargetCorrespondenceLabel.ToolTip = report.TargetEvidence is { MissingUiSources.Count: > 0 } evidence ?
            "未加入目标构建的 UI 源码：\n" + string.Join("\n", evidence.MissingUiSources) : TargetCorrespondenceLabel.Text;
        BuffersLabel.Text = $"LVGL 绘制缓存 {Size(report.DrawBufferBytes)} · PC 显示帧缓存 {Size(report.PcFramebufferBytes)}";
        EvidenceLabel.Text = report.Evidence;
        LimitationsLabel.Text = report.Limitations;
        TransferLabel.Text = report.DisplayTransfer is { } transfer ?
            $"完整一帧 {Size(transfer.FullFrameBytes)} · {transfer.RequestedFramesPerSecond} FPS 需要像素净荷 {Size(transfer.RequiredBytesPerSecond)}/s" +
            (transfer.CalibratedBytesPerSecond is { } bandwidth && transfer.FullFrameTransportLimitFps is { } limit ?
                $"\n校准带宽 {Size(bandwidth)}/s → 完整画面传输模型上限 {limit:0.##} FPS（不含渲染，不是 MCU 实测 FPS）\n校准来源：{transfer.CalibrationSource ?? "未注明"}" :
                "\n未设置实测传输带宽，MCU 刷新速度暂不估算。") : "传输模型等待工程配置。";
        if (report.PcStats is { } stats)
        {
            RenderStats(stats, report.PcPointerBits, report.LvglVersion);
        }
    }

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (service is null || project is not { } directory || busy || shutdown)
        {
            return;
        }
        var revision = projectRevision;
        operation = RunAsync(async token =>
        {
            var capture = await service.CaptureAsync(directory, token);
            if (shutdown || revision != projectRevision)
            {
                return;
            }
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(capture.Path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            ScreenshotImage.Source = bitmap;
            ScreenshotPath.Text = capture.Path;
            ScreenshotSection.Visibility = Visibility.Visible;
            ScreenshotSection.IsExpanded = true;
        });
        await operation;
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (shutdown || busy)
        {
            return;
        }
        busy = true;
        uiDiagnostic = uiNotice = "";
        using var source = new CancellationTokenSource();
        cancellation = source;
        UpdateButtons();
        try
        {
            await action(source.Token);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { uiDiagnostic = uiNotice = "已取消当前操作。"; }
        catch (Exception ex) { RecordDiagnostic(ex); }
        finally
        {
            cancellation = null;
            busy = false;
            if (!shutdown)
            {
                if (project is { } directory && service is not null)
                {
                    RenderSnapshot(service.GetSnapshot(directory));
                }
                RefreshToolchain();
                UpdateButtons();
            }
        }
    }

    private void PreviewChanged(object? sender, LvglPreviewSnapshot snapshot)
    {
        if (shutdown || !string.Equals(snapshot.Project, project, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        lock (snapshotSync)
        {
            pendingSnapshot = snapshot;
            if (snapshotQueued)
            {
                return;
            }
            snapshotQueued = true;
        }
        // 合并密集编译诊断；后台构建不逐行排队重画整个日志文本框。
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            LvglPreviewSnapshot? latest;
            lock (snapshotSync)
            {
                latest = pendingSnapshot;
                pendingSnapshot = null;
                snapshotQueued = false;
            }
            if (!shutdown && latest is not null && string.Equals(latest.Project, project, StringComparison.OrdinalIgnoreCase))
            {
                RenderSnapshot(latest);
            }
        }), DispatcherPriority.Background);
    }

    private void RenderSnapshot(LvglPreviewSnapshot snapshot)
    {
        StateLabel.Text = snapshot.Message + (uiNotice.Length == 0 ? "" : "\n" + uiNotice);
        StaleBanner.Visibility = snapshot.IsStale ? Visibility.Visible : Visibility.Collapsed;
        if (snapshot.Stats is { } stats)
        {
            RenderStats(stats, snapshot.PointerBits, snapshot.LvglVersion);
        }
        var log = snapshot.Log + (uiDiagnostic.Length == 0 ? "" : "\n" + uiDiagnostic);
        if (DiagnosticLog.Text != log)
        {
            var follow = DiagnosticLog.VerticalOffset >= DiagnosticLog.ExtentHeight - DiagnosticLog.ViewportHeight - 12;
            DiagnosticLog.Text = log;
            if (follow)
            {
                DiagnosticLog.ScrollToEnd();
            }
        }
        UpdateButtons(snapshot);
    }

    private void RenderStats(LvglPreviewStats stats, int pointerBits, string? lvglVersion)
    {
        PcRuntimeLabel.Text = $"LVGL {lvglVersion ?? "—"} · PC {pointerBits} bit · 已运行 {TimeSpan.FromMilliseconds(stats.UptimeMs):hh\\:mm\\:ss}";
        FpsLabel.Text = $"FPS {stats.Fps:0.##}";
        FramesLabel.Text = $"帧数 {stats.Frames:N0} · 刷新像素 {stats.FlushPixels:N0}";
        RuntimeDiagnostics.Text = $"警告 {stats.WarningCount:N0} · 错误 {stats.ErrorCount:N0}";
        if (!stats.HeapAvailable)
        {
            HeapLabel.Text = "LVGL 堆统计不可用";
            HeapBar.Value = 0;
            HeapDetails.Text = "当前自定义分配器未提供监测数据。";
            HeapPeakLabel.Text = "观测堆峰值：未知";
            return;
        }
        var total = stats.HeapUsedBytes + stats.HeapFreeBytes;
        HeapLabel.Text = $"LVGL 堆：{Size(stats.HeapUsedBytes)} / {Size(total)}";
        HeapBar.Value = total == 0 ? 0 : Math.Clamp(stats.HeapUsedBytes * 100d / total, 0, 100);
        HeapDetails.Text = $"空闲 {Size(stats.HeapFreeBytes)} · 最大空闲块 {Size(stats.HeapLargestFreeBytes)} · 碎片率 {stats.HeapFragmentationPercent:0.##}%";
        HeapPeakLabel.Text = "观测堆峰值：" + Size(stats.HeapObservedPeakBytes);
    }

    private void ResetStats()
    {
        PcRuntimeLabel.Text = "等待预览进程返回数据";
        FpsLabel.Text = "FPS —";
        FramesLabel.Text = "帧数 — · 刷新像素 —";
        HeapLabel.Text = "LVGL 堆：—";
        HeapBar.Value = 0;
        HeapDetails.Text = "空闲 / 最大空闲块 / 碎片率：—";
        HeapPeakLabel.Text = "观测堆峰值：—";
        RuntimeDiagnostics.Text = "警告 0 · 错误 0";
        ColorDepthLabel.Text = "—";
        BuffersLabel.Text = "LVGL 绘制缓存 — · PC 显示帧缓存 —";
        EvidenceLabel.Text = "";
        TransferLabel.Text = "传输模型等待工程配置。";
    }

    private void RecordDiagnostic(Exception ex)
    {
        uiDiagnostic = ex.ToString();
        uiNotice = ex.Message;
        StateLabel.Text = ex.Message;
        DiagnosticLog.AppendText("\n" + uiDiagnostic);
        LogDiagnostic?.Invoke("LVGL 预览：" + uiDiagnostic);
    }

    private void RefreshToolchain()
    {
        ToolchainLabel.Text = service?.ToolchainDisplayName ?? "自动使用 IDE 内置 PC 编译链";
        ToolchainLabel.ToolTip = service?.ToolchainPath;
    }

    private void UpdateButtons(LvglPreviewSnapshot? snapshot = null)
    {
        snapshot ??= project is { } directory && service is not null ? service.GetSnapshot(directory) : null;
        var ready = !shutdown && project is not null && configuration is not null;
        StartButton.IsEnabled = ready && !busy && !setupBusy && SetupView.Visibility != Visibility.Visible;
        StartButton.Content = snapshot?.IsRunning == true ? "重建预览" : "启动预览";
        StopButton.IsEnabled = !shutdown && !stopping && (busy || snapshot?.IsRunning == true);
        StopButton.Content = busy ? "取消 / 停止" : "停止";
        CaptureButton.IsEnabled = !shutdown && !busy && !setupBusy && snapshot?.IsRunning == true;
        ResourcesButton.IsEnabled = !shutdown && !busy && !setupBusy && project is not null;
        SetupButton.IsEnabled = !shutdown && !busy && !setupBusy && project is not null;
        SetupButton.Content = SetupView.Visibility == Visibility.Visible ? "收起配置向导" : "配置自定义 UI";
        ToolchainButton.IsEnabled = !shutdown && !busy && !setupBusy;
        BundledToolchainButton.IsEnabled = !shutdown && !busy && !setupBusy && service?.UsingBundledToolchain == false;
        ConfigurationInputs.IsEnabled = ApplyButton.IsEnabled = ready && !busy && !setupBusy && SetupView.Visibility != Visibility.Visible;
    }

    public async Task ShutdownAsync()
    {
        if (shutdown)
        {
            return;
        }
        shutdown = true;
        if (service is not null)
        {
            service.Changed -= PreviewChanged;
        }
        cancellation?.Cancel();
        projectLoadCancellation?.Cancel();
        await SetupView.ShutdownAsync();
        await Task.WhenAll(operation, projectLoad);
        projectLoadCancellation?.Dispose();
        projectLoadCancellation = null;
    }

    public async Task StopProjectAsync(CancellationToken token)
    {
        cancellation?.Cancel();
        projectLoadCancellation?.Cancel();
        await SetupView.CancelAsync();
        await Task.WhenAll(operation, projectLoad);
        if (project is { } directory && service is not null)
        {
            await service.StopAsync(directory, token);
        }
    }

    private static string Size(long bytes) => bytes < 1024 ? $"{bytes:N0} B" : bytes < 1024 * 1024 ?
        (bytes / 1024d).ToString("0.##", CultureInfo.InvariantCulture) + " KiB" :
        (bytes / (1024d * 1024)).ToString("0.##", CultureInfo.InvariantCulture) + " MiB";

    internal async Task InspectSetupForPreviewAsync(string librarySearchDirectory, string uiDirectory)
    {
        await projectLoad;
        if (project is null)
        {
            throw new StudioXException("PROJECT_REQUIRED", "离线配置页面验证需要打开工程。");
        }
        SetupView.SetProject(project, configuration);
        SetupView.Visibility = Visibility.Visible;
        await SetupView.InspectForPreviewAsync(librarySearchDirectory, uiDirectory);
        UpdateButtons();
    }

    internal int SetupCandidateCount => SetupView.CandidateCount;
    internal int SetupSourceCount => SetupView.SourceCount;
    internal void ShowSetupStepForPreview(int value) => SetupView.ShowStepForPreview(value);
}
