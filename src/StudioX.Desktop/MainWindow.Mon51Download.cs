namespace StudioX.Desktop;

using StudioX.Foundation;
using System.Windows.Threading;

public partial class MainWindow
{
    private async Task StartMon51DebugAsync(CancellationToken token)
    {
        var root = RequireProject();
        var ports = await StudioX.Application.Serial.SerialTerminalService.ListPortsAsync();
        var firmwareSources = await services.Mon51Setup.ListFirmwareSourcesAsync(root, token);
        var dialog = new Mon51ConnectionWindow(ports, loadedStcIspSettings?.Port ?? "", System.IO.Path.GetFileName(root),
            firmwareSources,
            async (port, baud, target, progress, cancel) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancel);
                await SaveAllSourcesAsync(root, linked.Token);
                await PersistBreakpointLinesAsync();
                ApplyBuildSettings(await services.StcBuilds.PrepareDebugAsync(root, linked.Token));
                ShowBottom(0);
                BuildLog.Clear();
                var build = await BuildWithSummaryAsync(root, linked.Token);
                if (build.LogPath is { } logPath) {Log("构建日志：" + logPath);}
                Log(build.Summary);
                if (!build.Success) {throw new StudioXException("MON51_BUILD", "最新源码编译失败，未连接或覆盖目标程序。\n" + build.Summary);}
                var preparation = await services.Debugger.PrepareMon51DownloadAsync(linked.Token);
                progress.Report($"用户程序 {preparation.UserBytes} 字节；BIN SHA-256：{preparation.BinarySha256}\n正在通过 Mon51 更新用户区并回读核对…");
                if (editorDocuments.Any(document => document.IsDirty))
                    {throw new StudioXException("MON51_SOURCE_CHANGED", "编译期间出现未保存编辑，请重新开始调试；未连接或覆盖目标。");}
                await services.Debugger.DownloadAndStartMon51Async(port, baud, target, preparation, linked.Token);
            }, async (port, target, firmware, progress, cancel) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancel);
                progress.Report("正在校验器件包内监控固件：" + firmware.DisplayName);
                var prepared = await services.Mon51Setup.PrepareAsync(root, port, target, firmware, linked.Token);
                progress.Report("专用制作通道开始等待；请给 MCU 断电再上电进入 ISP。制作完成后还需再断电上电激活监控。");
                var result = await services.Mon51Setup.ExecuteAsync(prepared, progress, linked.Token);
                Log("仿真芯片制作日志：" + result.LogPath);
            }) { Owner = this };
        dialog.ShowDialog();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        RefreshDebugUi();
        Status.Text = services.Debugger.Reason;
    }
}
