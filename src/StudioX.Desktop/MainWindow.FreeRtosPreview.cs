namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine.Debugging;

public partial class MainWindow
{
    /// <summary>界面样例与裸机离线会话分别验证；样例不是运行内核或实板读数。</summary>
    public async Task RenderFreeRtosPreviewAsync(string directory, string packArchive)
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            checks++;
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await debugNavigationTask; await debugRtosTask;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await debugRtosTask;
        }
        var exampleRoot = Path.Combine(services.DataDirectory, "debug-examples");
        var existing = Directory.Exists(exampleRoot) ? Directory.EnumerateDirectories(exampleRoot)
            .FirstOrDefault(path => File.Exists(Path.Combine(path, ".studiox", "project.json"))) : null;
        // 同一预览目录复跑时复用已经生成的只用于测试的工程，避免再次复制整份 SDK。
        if (existing is null) await ShowDebugDemoAsync(packArchive);
        else
        {
            await OpenProjectAsync(existing, CancellationToken.None);
            await services.Debugger.StartOfflineAsync();
        }
        await Settle();
        DebugTools.ShowFreeRtos(); await Settle();
        var view = DebugTools.FreeRtosView;
        Check(view.CanRead && view.TaskCount == 0 && view.ObjectCount == 0 && view.Status.Contains("未找到", StringComparison.Ordinal),
            "Bare-metal offline demo must report unavailable RTOS, preserving its session");
        Check(view.Diagnostics.Contains("FreeRTOS", StringComparison.Ordinal) && services.Debugger.State == DebugState.Stopped,
            "Missing-kernel diagnostics must be visible without ending the debug session");
        view.ObserveSymbol("vTaskDelay(1)"); await Settle();
        Check(view.ObjectSymbols.Count == 0 && view.Status.Contains("不支持函数", StringComparison.Ordinal), "Object input rejects executable expression inline");
        view.ObserveSymbol("sensorQueue"); await Settle();
        Check(view.ObjectSymbols.SequenceEqual(["sensorQueue"]) && view.CanRead, "Explicit global handle is retained for this project");
        var sample = new FreeRtosSnapshot(true, null, true, 0, 15824, 0x20000100, 5,
        [
            new(0x20000100, "sensor", "Running", 3, 2, 0x20001000, 0x200012e0, 256, 1024, 10400),
            new(0x20000200, "display", "Blocked", 2, 2, 0x20001400, 0x200017a0, 384, 1024, 2800),
            new(0x20000300, "logger", "Ready", 1, 1, 0x20001800, 0x200019c0, 128, 512, 2100),
            new(0x20000400, "worker", "Suspended", 1, 1, 0x20001a00, 0x20001bc0, null, null, null),
            new(0x20000500, "IDLE", "Ready", 0, 0, 0x20001c00, 0x20001dc0, 288, 512, 1300)
        ],
        new("heap_4", 12288, 7680, 5120, 32, 18, 4096, 3),
        [
            new(0x20002000, "sensorQueue", "Queue", 3, 8, 16, 0, 1, null, null),
            new(0x20002080, "displayReady", "BinarySemaphore", 0, 1, 0, 0, 1, null, null),
            new(0x20002100, "busMutex", "Mutex", 0, 1, 0, 0, 2, 0x20000100, 0)
        ],
        ["离线界面样例：数值由预览构造，不来自正在运行的 FreeRTOS，也不代表实板验证。", "worker 的栈边界与填充证据未提供，所以栈字段显示 —；不会补为 0。"]);
        view.SetSnapshot(sample, false, sample: true);
        Check(view.TaskCount == 5 && view.ObjectCount == 3 && view.Scheduler.Contains("sensor", StringComparison.Ordinal) &&
            view.Status.Contains("离线界面样例", StringComparison.Ordinal), "Sample rows and evidence labels");
        view.SetLoading();
        Check(!view.CanRead && view.IsReading, "Read controls are disabled while a snapshot is loading");
        view.SetCancelPending();
        Check(view.Status.Contains("只读命令返回", StringComparison.Ordinal), "Cancellation explains protocol boundary");
        view.SetCanceled();
        Check(view.CanRead && view.TaskCount == 5 && view.Status.Contains("取消", StringComparison.Ordinal), "Cancel retains prior data and restores reading");
        view.SetSnapshot(sample, false, sample: true);
        savedBottomHeight = 390; ShowBottom(2);
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme); UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            for (var page = 0; page < 4; page++)
            {
                view.ShowPage(page); UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                UpdateLayout();
                if (page == 2) Check(view.ObjectColumnsReadable, "Object headers and values cannot shrink on first tab selection");
                Render(DebugTools, Path.Combine(directory, $"rtos-{page}-{theme.Id}.png"));
            }
            view.ShowPage(0); UpdateLayout(); Render(this, Path.Combine(directory, $"rtos-window-{theme.Id}.png"));
        }
        ApplyTheme(ThemeService.Dark); Width = 1100; Height = 720; savedBottomHeight = 280; ShowBottom(2); UpdateLayout();
        Check(view.ActualWidth >= 300 && view.ActualHeight > 140, "RTOS pane must remain usable at minimum window size");
        Check(BottomPanel.TranslatePoint(new Point(0, BottomPanel.ActualHeight), WindowRoot).Y <= WindowRoot.ActualHeight - 24,
            "RTOS view cannot cover status bar");
        Render(this, Path.Combine(directory, "rtos-minimum.png"));
        await services.Debugger.ChangeBreakpointAsync(services.Debugger.Breakpoints.Single().Id, false);
        await services.Debugger.ExecuteAsync(DebugAction.Continue); await Settle();
        Check(!view.CanRead && view.TaskCount == 5 && view.SnapshotOpacity < 1 && view.Status.Contains("上次暂停快照", StringComparison.Ordinal),
            "Running must retain faded old RTOS data and prohibit read");
        await services.Debugger.ExecuteAsync(DebugAction.Pause);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
            while (services.Debugger.State != DebugState.Stopped) await Task.Delay(20, timeout.Token);
        await Settle();
        Check(view.CanRead && view.TaskCount == 0 && view.Status.Contains("未找到", StringComparison.Ordinal), "New pause automatically reads actual bare-metal state, replacing UI sample");
        view.SetSnapshot(sample, false, sample: true);
        await services.Debugger.StopAsync(); await Settle();
        Check(!view.CanRead && view.TaskCount == 0 && view.ObjectCount == 0, "End debugging clears RTOS data");
        await CloseProjectAsync(CancellationToken.None); await Settle();
        Check(view.ObjectSymbols.Count == 0, "Closing project clears its manually observed handles");
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"),
            $"PASS {checks} RTOS WPF checks: bare-metal unavailable; inline object input validation; project-scoped handles; task/heap/object/diagnostic rendering; dark/light/minimum size; running stale view; pause automatic refresh; stop/project cleanup. Samples are explicitly labelled. No hardware accessed.\n");
    }
}
