namespace StudioX.Application.StcDebugging;

using StudioX.Engine;
using StudioX.Foundation;

/// <summary>复用工程下载设置，先握手；仅握手失败才制作监控，制作后必须结束并等待人工重新上电。</summary>
public sealed class Mon51LaunchService(DebugSessionService debugger, IMon51SetupService setup)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<Mon51LaunchResult> StartAsync(string project,
        Func<CancellationToken, Task<Mon51DownloadPreparation>> prepareUserProgram,
        IProgress<string>? output = null, CancellationToken token = default,
        Func<StcIspPreparation, CancellationToken, Task<StcIspReport>>? installMonitor = null)
    {
        if (!await gate.WaitAsync(0, token))
        {
            throw new StudioXException("MON51_BUSY", "正在准备串口仿真，请等待当前操作完成。");
        }
        try
        {
            var manifest = await ProjectService.ReadAsync(project, token);
            if (!StcMon51Targets.Supports(manifest.DeviceId) || manifest.ToolsetId != "stc.sdcc")
            {
                throw new StudioXException("MON51_TARGET", "串口仿真仅支持 " + StcMon51Targets.DisplayNames + "。");
            }
            if (!string.Equals(Path.GetFullPath(project), debugger.ProjectDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new StudioXException("MON51_PROJECT", "调试会话与当前工程不一致，请重新打开工程。");
            }
            var settings = await StcIspSettings.ReadAsync(project, token);
            if (string.IsNullOrWhiteSpace(settings.Port))
            {
                return Mon51LaunchResult.PortRequired;
            }
            if (debugger.IsActive)
            {
                throw new StudioXException("MON51_BUSY", "请先结束当前调试。");
            }
            output?.Report($"Mon51 握手：{manifest.DeviceId} · {settings.Port} · {settings.TransferBaud} baud（与下载设置一致）");
            var started = await debugger.TryBuildAndStartMon51Async(settings.Port, settings.TransferBaud, manifest.DeviceId,
                async cancel =>
                {
                    await RequireUnchangedAsync(cancel);
                    output?.Report("监控握手成功，正在编译并更新当前工程用户程序…");
                    var preparation = await prepareUserProgram(cancel);
                    await RequireUnchangedAsync(cancel);
                    return preparation;
                }, token);
            if (started)
            {
                return Mon51LaunchResult.Started;
            }
            output?.Report("监控握手失败：" + debugger.Reason);
            await RequireUnchangedAsync(token);
            var prepared = await setup.PrepareForProjectAsync(project, settings, token);
            await RequireUnchangedAsync(token);
            token.ThrowIfCancellationRequested();
            output?.Report($"正在为 {manifest.DeviceId} 下载对应仿真监控。下载工具开始等待后，请给 MCU 断电再上电，进入 ISP 下载模式（第一次重新上电，用于下载）。");
            // 调用方可以复用普通 ISP 的流式输出呈现，流程与制作成功门禁仍由应用层负责。
            var result = installMonitor is null ? await setup.ExecuteAsync(prepared, output, token) : await installMonitor(prepared, token);
            if (!result.Success || !result.Log.Contains("STUDIOX_MONITOR_SETUP_COMPLETE POWER_CYCLE_REQUIRED", StringComparison.Ordinal))
            {
                throw new StudioXException("MON51_SETUP_FAILED", "监控制作未确认完成，请查看原始日志：" + result.LogPath + "\n" + result.Log);
            }
            await debugger.StopAsync();
            output?.Report("STC 仿真监控下载成功。请给 MCU 本体断电约 2 秒再上电（第二次重新上电，由仿真固件要求；复位键不能代替），然后再次点击调试。本次调试已退出，尚未连接新监控。\n制作日志：" + result.LogPath);
            return Mon51LaunchResult.MonitorInstalled;

            async Task RequireUnchangedAsync(CancellationToken cancel)
            {
                if (!string.Equals(Path.GetFullPath(project), debugger.ProjectDirectory, StringComparison.OrdinalIgnoreCase) ||
                    await ProjectService.ReadAsync(project, cancel) != manifest || await StcIspSettings.ReadAsync(project, cancel) != settings)
                {
                    throw new StudioXException("MON51_SETTINGS_CHANGED", "工程器件或下载串口/波特率已改变，请重新点击调试。");
                }
            }
        }
        finally { gate.Release(); }
    }
}
