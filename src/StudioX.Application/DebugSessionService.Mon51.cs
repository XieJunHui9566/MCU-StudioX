namespace StudioX.Application;

using StudioX.Application.StcDebugging;
using StudioX.Devices;
using StudioX.Engine;
using StudioX.Foundation;

public sealed partial class DebugSessionService
{
    private StudioX.Engine.Debugging.DebugSnapshot? lastMonitorSnapshot;
    public Mon51DebugSession? MonitorSession
    {
        get; private set;
    }

    public Task StartMon51Async(string port, int baud, string confirmedTarget, CancellationToken token = default)
        => StartMon51CoreAsync(port, baud, confirmedTarget, null, token);

    public async Task<Mon51DownloadPreparation> PrepareMon51DownloadAsync(CancellationToken token = default)
        => (await Mon51DownloadPreparation.ReadAsync(ProjectDirectory ?? throw Error("请先打开 STC 工程。"), token)).Preparation;

    public Task DownloadAndStartMon51Async(string port, int baud, string confirmedTarget, Mon51DownloadPreparation preparation, CancellationToken token = default)
        => StartMon51CoreAsync(port, baud, confirmedTarget, preparation, token);

    private async Task StartMon51CoreAsync(string port, int baud, string confirmedTarget, Mon51DownloadPreparation? preparation, CancellationToken token)
    {
        if (confirmedTarget != "IAP15F2K61S2")
        {
            throw new StudioXException("MON51_TARGET", "请选择本次验证的 IAP15F2K61S2 仿真芯片；其它器件尚未完成监控适配验证。");
        }
        var settings = new SerialSettings(port, baud);
        settings.Validate();
        await gate.WaitAsync(token);
        try
        {
            if (IsActive || adapter is not null || MonitorSession is not null)
            {
                throw Error("请先结束已有调试会话。");
            }
            if (devices is null || ProjectDirectory is null)
            {
                throw Error("请先打开 STC SDCC 工程。");
            }
            var project = await ProjectService.ReadAsync(ProjectDirectory, token);
            if (project.ToolsetId != "stc.sdcc")
            {
                throw new StudioXException("MON51_PROJECT", "当前监控调试入口只适用于 STC SDCC 工程。");
            }
            StcDebugArtifact? downloadArtifact = null;
            if (preparation is not null)
            {
                var current = await Mon51DownloadPreparation.ReadAsync(ProjectDirectory, token);
                if (current.Preparation != preparation)
                {
                    throw new StudioXException("MON51_ARTIFACT_CHANGED", "确认下载后工程、源码或固件已改变；请重新编译和确认，未连接或擦除目标。");
                }
                downloadArtifact = current.Artifact;
            }
            var monitor = new Mon51DebugSession(dataDirectory) { UserDownloadRequested = preparation is not null };
            var transport = monitorTransportFactory?.Invoke(settings) ?? new SerialTransport(settings);
            // 连接快照会更新界面状态；先保留工程偏好，避免首次空快照覆盖已保存断点和观察项。
            var savedBreakpoints = breakpoints.ToArray();
            var savedWatches = watches.ToArray();
            MonitorSession = monitor;
            IsHardware = !transport.IsSimulated;
            HardwareTarget = null;
            HardwareTargetName = confirmedTarget + " · " + port;
            monitor.Changed += UpdateMonitorState;
            monitor.Output += Trace;
            try
            {
                await monitor.ConnectAsync(devices, transport, ProjectDirectory, token);
                if (downloadArtifact is not null)
                {
                    await monitor.DownloadUserProgramAsync(downloadArtifact, token);
                }
            }
            catch (Exception error)
            {
                // 失败连接已释放串口；清除 facade 占位并恢复偏好，使窗口能直接重试。
                monitor.Changed -= UpdateMonitorState;
                monitor.Output -= Trace;
                try
                {
                    await monitor.DisposeAsync();
                }
                catch (Exception cleanup) { Trace("连接失败后的清理诊断：" + cleanup); }
                MonitorSession = null;
                IsHardware = false;
                HardwareTargetName = null;
                lastMonitorSnapshot = null;
                Snapshot = StudioX.Engine.Debugging.DebugSnapshot.Empty;
                breakpoints = savedBreakpoints;
                watches = savedWatches;
                SessionLogPath = monitor.LogPath;
                SetState(StudioX.Engine.Debugging.DebugState.Faulted, error.Message + " 原始日志：" + monitor.LogPath);
                throw;
            }
            monitor.PreferencesChanged = async () =>
            {
                breakpoints = monitor.SourceBreakpoints.ToArray();
                watches = monitor.Watches.ToArray();
                await SaveAsync(CancellationToken.None);
            };
            await monitor.SetPreferencesAsync(savedBreakpoints, savedWatches, token);
            try
            {
                await LoadMon51SymbolsCoreAsync(monitor, token);
            }
            catch (StudioXException ex) when (IsMon51SymbolError(ex)) { }
            SessionLogPath = monitor.LogPath;
        }
        finally { gate.Release(); }
    }

    public async Task LoadMon51SymbolsAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            await LoadMon51SymbolsCoreAsync(MonitorSession ?? throw Error("请先连接 Mon51。"), token);
        }
        finally { gate.Release(); }
    }

    private static bool IsMon51SymbolError(StudioXException error) => error.Code is "MON51_SOURCE_CHANGED" or "MON51_SYMBOLS" or "MON51_ARTIFACT_CHANGED" or "MON51_IMAGE_MISMATCH" or "MON51_CDB";

    private async Task LoadMon51SymbolsCoreAsync(Mon51DebugSession monitor, CancellationToken token)
    {
        try
        {
            await monitor.LoadSymbolsAsync(await StcDebugArtifacts.ReadAsync(ProjectDirectory!, token), token);
        }
        catch (StudioXException error) when (IsMon51SymbolError(error))
        {
            await monitor.ReportSymbolsUnavailableAsync(error.Message, token);
            Trace("源码符号未启用，当前为地址调试：" + error);
            throw;
        }
    }

    private void UpdateMonitorState()
    {
        if (MonitorSession is not { } monitor)
        {
            return;
        }
        if (!ReferenceEquals(lastMonitorSnapshot, monitor.Snapshot))
        {
            lastMonitorSnapshot = monitor.Snapshot;
            Snapshot = NormalizeSnapshot(monitor.Snapshot);
        }
        breakpoints = monitor.SourceBreakpoints.ToArray();
        watches = monitor.Watches.ToArray();
        SessionLogPath = monitor.LogPath;
        SetState(monitor.State, monitor.Reason);
    }

    private void RequireSourceDebugCapability()
    {
        if (MonitorSession is not null)
        {
            throw new StudioXException("MON51_CAPABILITY", "此操作需要 GDB 调试后端；Mon51 会话请使用 8051 存储区接口。外设寄存器描述、RTOS 和 GDB 插件操作暂未适配 Mon51。");
        }
    }
}
