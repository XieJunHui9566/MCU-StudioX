namespace StudioX.Desktop;

using System.Windows.Threading;
using StudioX.Application.Output;
using StudioX.Engine;

public partial class MainWindow
{
    private bool IsPicProject => currentProjectManifest?.Kind == ProjectKind.Pic;

    private async Task<bool> ShowPicDownloadSettingsAsync(string root, CancellationToken token)
    {
        var settings = await services.PicDownloads.LoadSettingsAsync(root, token);
        var programmers = await services.PicDownloads.ListAsync(token: token);
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
        var settings = await services.PicDownloads.LoadSettingsAsync(root, token);
        if (settings.ProgrammerSerial.Length == 0 && !await ShowPicDownloadSettingsAsync(root, token))
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
        if (new PicDownloadConfirmWindow(preview) { Owner = this }.ShowDialog() != true)
        {
            Status.Text = "PICkit3 下载已取消。";
            return;
        }
        EnsureNoActiveDebug();
        if (closing || closed || projectDirectory != root || editorDocuments.Any(document => document.IsDirty))
        {
            return;
        }
        var lines = new OutputLineBuffer();
        var output = new Progress<string>(text => { foreach (var line in lines.Append(text)) { Log(line); } });
        BeginBuildOutput("PICkit3 回读备份、写入与校验", "下载中");
        try
        {
            var report = await services.PicDownloads.DownloadApprovedAsync(root, preview.ApprovalId, output, token);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (lines.Flush() is { } tail)
            {
                Log(tail);
            }
            Log("回读备份：" + report.BackupPath);
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
