namespace StudioX.Application;

using StudioX.Application.Tools;
using StudioX.Engine;
using StudioX.Foundation;

public sealed partial class ToolEnvironmentService
{
    public Task<(IReadOnlyList<ToolRepairRecovery> Items, IReadOnlyList<string> Diagnostics)> InspectRecoveryAsync(CancellationToken token = default)
        => Task.Run(() => ToolRepairTransactions.InspectAsync(catalog.RootDirectory, token), token);

    public Task RecoverAsync(ToolRepairRecovery preview, IProgress<string>? progress = null, CancellationToken token = default)
        => RecoverCoreAsync(preview, restorePrevious: false, progress, token);
    public Task RestorePreviousAsync(ToolRepairRecovery preview, IProgress<string>? progress = null, CancellationToken token = default)
        => RecoverCoreAsync(preview, restorePrevious: true, progress, token);
    private Task RecoverCoreAsync(ToolRepairRecovery preview, bool restorePrevious, IProgress<string>? progress, CancellationToken token)
        => Task.Run(async () =>
        {
            await gate.WaitAsync(token);
            try
            {
                var root = catalog.RootDirectory;
                var record = await ToolRepairTransactions.ReadAsync(root, preview.TransactionId, token);
                using var lease = ToolUsageLease.Acquire(ToolRepairTransactions.Target(root, record), maintenance: true);
                var fresh = await ToolRepairTransactions.PreviewAsync(root, record, token);
                if (record.State != "prepared" || fresh != preview)
                {
                    throw new StudioXException("TOOLS_RECOVERY_CHANGED", "恢复记录或组件目录在预览后变化，请刷新后重试。");
                }
                if (!fresh.CanRecover)
                {
                    throw new StudioXException("TOOLS_RECOVERY_BLOCKED", fresh.Detail);
                }
                if (restorePrevious && !fresh.CanRestorePrevious)
                {
                    throw new StudioXException("TOOLS_RECOVERY_BLOCKED", "当前事务没有可恢复的原组件，未修改目录。");
                }
                var action = restorePrevious ? "restore" : fresh.Action;
                if (action != "keep")
                {
                    var candidate = action == "finish" ? ToolRepairTransactions.Stage(root, record.TransactionId)
                        : action == "restore" ? ToolRepairTransactions.Backup(root, record.TransactionId) : root;
                    // 目标独占租约已持有；候选目录独立核验，已就位目标用同一目录的维护校验入口。
                    using var candidateLease = candidate == root ? null : ToolUsageLease.Acquire(
                        PathBoundary.Resolve(candidate, record.Id + "/" + record.Version), maintenance: true);
                    var resolved = await new ToolsetCatalog(candidate).VerifyForMaintenanceAsync(record.Id, record.Version,
                        record.CompilerId, candidateLease ?? lease, token, progress);
                    var expected = action == "restore" ? record.PreviousFingerprint : record.Fingerprint;
                    if (resolved.Fingerprint != expected)
                    {
                        throw new StudioXException("TOOLS_RECOVERY_CHANGED", "恢复组件的内容指纹已变化。");
                    }
                    token.ThrowIfCancellationRequested();
                    if (fresh != await ToolRepairTransactions.PreviewAsync(root, record, token))
                    {
                        throw new StudioXException("TOOLS_RECOVERY_CHANGED", "校验期间恢复输入发生变化。");
                    }
                    if (action is "finish" or "restore")
                    {
                        var target = ToolRepairTransactions.Target(root, record);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        Directory.Move(PathBoundary.Resolve(candidate, record.Id + "/" + record.Version), target);
                    }
                }
                else
                {
                    token.ThrowIfCancellationRequested();
                }
                await ToolRepairTransactions.WriteAsync(root, record with
                {
                    State = action is "keep" ? "rolled-back" : action is "restore" ? "restored" : "committed"
                });
                progress?.Report((restorePrevious ? "恢复原组件" : fresh.ActionText) + "完成；恢复记录和原备份保留。");
            }
            finally { gate.Release(); }
        }, token);
}
