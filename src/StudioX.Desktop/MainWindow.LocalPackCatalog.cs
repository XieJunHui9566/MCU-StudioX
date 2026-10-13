namespace StudioX.Desktop;

using StudioX.Packages;

public partial class MainWindow
{
    private bool bundledPacksChecked;
    private CancellationTokenSource? bundledPackCancellation;
    private Task bundledPackTask = Task.CompletedTask;

    private void StartBundledPackCheck(Func<CancellationToken, Task<BundledPackImportResult>>? import = null)
    {
        if (closing || bundledPacksChecked || bundledPackCancellation is not null)
        {
            return;
        }
        var cancellation = new CancellationTokenSource();
        bundledPackCancellation = cancellation;
        LocalPackStatus.Text = "正在后台检查随附器件包；可继续选择已安装器件。";
        import ??= token => services.Packs.ImportBundledMissingAsync(Path.Combine(AppContext.BaseDirectory, "device-packs"), token);
        bundledPackTask = RunBundledPackCheckAsync(import, cancellation);
    }

    private async Task RunBundledPackCheckAsync(Func<CancellationToken, Task<BundledPackImportResult>> import,
        CancellationTokenSource cancellation)
    {
        try
        {
            // 归档哈希与 SDK 文件检查在工作线程继续，不占用新建工程操作或 WPF 线程。
            var result = await Task.Run(() => import(cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            bundledPacksChecked = true;
            if (closing)
            {
                return;
            }
            if (result.Imported > 0)
            {
                Log($"已导入 {result.Imported} 个随附器件包。");
                await pendingOperation.WaitAsync(cancellation.Token);
                // 已打开工程时保留工程信息；下次新建会读取最新目录。
                if (!closing && projectDirectory is null)
                {
                    await RefreshPacksAsync(cancellation.Token, preserveSelection: true);
                }
            }
            foreach (var failure in result.Failures)
            {
                Log($"随附器件包导入失败：{failure.File}：{failure.Message}");
            }
            LocalPackStatus.Text = result.Failures.Count > 0
                ? "部分随附器件包检查失败；已安装器件仍可选择。可手动导入器件包。"
                : result.Imported > 0 ? $"随附器件包检查完成，新增 {result.Imported} 个器件包。" : "";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!closing)
            {
                LocalPackStatus.Text = "已停止随附器件包检查；已安装器件仍可选择。";
            }
        }
        catch (Exception ex)
        {
            if (!closing)
            {
                LocalPackStatus.Text = "随附器件包检查失败；已安装器件仍可选择。可手动导入器件包。";
                Log("读取随附器件包失败：" + ex);
            }
        }
        finally
        {
            if (ReferenceEquals(bundledPackCancellation, cancellation))
            {
                bundledPackCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private async Task StopBundledPackCheckAsync()
    {
        bundledPackCancellation?.Cancel();
        await bundledPackTask;
    }

    private async Task PrunePackVersionsAsync(CancellationToken token)
    {
        // 只在用户导入或同步后清理；后台浏览不能删除正在被选择、校验或复制的包。
        var cleanup = await services.Packs.PruneSupersededAsync(token);
        if (cleanup.Removed.Count > 0)
        {
            Log($"已清理 {cleanup.Removed.Count} 个重复旧器件包，释放 {cleanup.ReclaimedBytes / 1024d / 1024:F1} MiB。");
        }
        foreach (var failure in cleanup.Failures)
        {
            Log($"旧器件包清理失败：{failure.Id} {failure.Version}：{failure.Message}");
        }
    }
}
