namespace StudioX.Desktop;

using System.Windows.Threading;
using StudioX.Application.Output;
using StudioX.Engine;

public partial class MainWindow
{
    private bool IsPicProject => currentProjectManifest?.Kind == ProjectKind.Pic;

    private async Task StartPicDebugAsync(string root, CancellationToken token)
    {
        await SaveAllSourcesAsync(root, token);
        await PersistBreakpointLinesAsync();
        if (!await ShowPicDownloadSettingsAsync(root, token))
        {
            return;
        }
        var settings = await services.PicDownloads.LoadSettingsAsync(root, token);
        BeginBuildOutput("PIC CMake + Ninja 调试构建", "编译中");
        var lines = new OutputLineBuffer();
        var output = new Progress<string>(text => { foreach (var line in lines.Append(text)) { Log(line); } });
        var build = await services.Builds.BuildPicDebugAsync(root, output, token);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (lines.Flush() is { } tail)
        {
            Log(tail);
        }
        FinishBuildOutput(build.Success ? "PIC 调试映像已生成" : "PIC 调试构建失败", build.Success);
        if (!build.Success)
        {
            return;
        }
        await services.Debugger.StartPicAsync(services.PicDebug, settings.ProgrammerSerial, token);
        await NavigateSelectedDebugFrameAsync();
        Status.Text = services.Debugger.Reason;
    }

    private async Task<bool> ShowPicDownloadSettingsAsync(string root, CancellationToken token)
    {
        var settings = await services.PicDownloads.LoadSettingsAsync(root, token);
        var programmers = await services.PicDownloads.ListAsync(token: token);
        if (StudioX.Engine.Pic.PicProgrammerSelection.Automatic(programmers) is { } automatic)
        {
            await services.PicDownloads.SaveSettingsAsync(root, automatic.Serial, settings.ReleaseAfterProgramming, token);
            Status.Text = "已自动选择 PICkit3 · " + automatic.Serial;
            return true;
        }
        var dialog = new PicDownloadWindow(settings, programmers, () => services.PicDownloads.ListAsync(token: token)) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return false;
        }
        await services.PicDownloads.SaveSettingsAsync(root, dialog.Serial, dialog.Release, token);
        Status.Text = "已保存 PICkit3 下载设置。";
        return true;
    }

    private async Task DownloadPicAsync(string root, CancellationToken token)
    {
        await SaveAllSourcesAsync(root, token);
        if (!await ShowPicDownloadSettingsAsync(root, token))
        {
            return;
        }
        ShowBottom(0);
        BuildLog.Clear();
        var build = await BuildWithSummaryAsync(root, token);
        if (!build.Success)
        {
            return;
        }
        var preview = await services.PicDownloads.PreviewAsync(root, token);
        EnsureNoActiveDebug();
        if (closing || closed || projectDirectory != root || editorDocuments.Any(document => document.IsDirty))
        {
            return;
        }
        var lines = new OutputLineBuffer();
        var output = new Progress<string>(text => { foreach (var line in lines.Append(text)) { Log(line); } });
        BeginBuildOutput("PICkit3 直接写入与校验", "下载中");
        try
        {
            var report = await services.PicDownloads.DownloadApprovedAsync(root, preview.ApprovalId, output, token);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (lines.Flush() is { } tail)
            {
                Log(tail);
            }
            Log("下载日志：" + report.LogPath);
            Status.Text = report.Summary;
            FinishBuildOutput(report.Summary, report.Success);
        }
        catch (Exception failure)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (lines.Flush() is { } tail)
            {
                Log(tail);
            }
            if (failure.Data["PicDownloadLog"] is string path)
            {
                Log("下载日志：" + path);
            }
            FinishBuildOutput("PICkit3 下载未确认完成，原始诊断见输出及工程下载日志", false);
            throw;
        }
    }
}
