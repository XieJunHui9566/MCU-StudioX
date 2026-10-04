namespace StudioX.Application.Tools;

using StudioX.Engine;
using StudioX.Foundation;

public sealed partial class ToolManagementService
{
    public Task<string> RetireAsync(ManagedToolVersion selected, string? currentProject, CancellationToken token = default)
        => MoveToRecoveryAsync(selected, currentProject, reviewedRemoval: false, token);

    /// <summary>用户已审阅影响的显式卸载；允许最新或有依赖的版本，保留工程声明和内容锁。</summary>
    public Task<string> RemoveAsync(ManagedToolVersion selected, string? currentProject, CancellationToken token = default)
        => MoveToRecoveryAsync(selected, currentProject, reviewedRemoval: true, token);

    private Task<string> MoveToRecoveryAsync(ManagedToolVersion selected, string? currentProject, bool reviewedRemoval, CancellationToken token)
        => Task.Run(async () =>
        {
            await gate.WaitAsync(token);
            try
            {
                EnsureIdle();
                var fresh = await FindFreshAsync(selected, currentProject, token);
                if (!(reviewedRemoval ? fresh.CanRemove : fresh.CanRetire))
                {
                    throw new StudioXException("TOOLS_PROTECTED", "该版本正在占用或检查未完成；常规清理还会保留最新和有依赖的版本。");
                }
                if (reviewedRemoval)
                {
                    RequireReviewedDependencies(selected, fresh);
                }
                var root = VersionRoot(fresh);
                using var lease = ToolUsageLease.Acquire(root, maintenance: true);
                CheckStamp(root, fresh, token);
                var retirementId = Guid.NewGuid().ToString("N");
                var parent = PathBoundary.Resolve(catalog.RootDirectory, ".retired/" + retirementId);
                var destination = PathBoundary.Resolve(parent, fresh.Id + "/" + fresh.Version);
                var moved = false;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    token.ThrowIfCancellationRequested();
                    Directory.Move(root, destination);
                    moved = true;
                    await JsonStore.WriteAsync(PathBoundary.Resolve(parent, "retirement.json"),
                        new ToolRetirement(1, fresh.Id, fresh.Version, fresh.CompilerId, fresh.Fingerprint, DateTimeOffset.UtcNow), token);
                }
                catch (Exception failure)
                {
                    try
                    {
                        if (moved)
                        {
                            Directory.Move(destination, root);
                        }
                        if (Directory.Exists(parent))
                        {
                            _ = CaptureTree(parent, CancellationToken.None);
                            Directory.Delete(parent, recursive: true);
                        }
                    }
                    catch (Exception restoreFailure) { throw new AggregateException("移动后记录失败，回退也失败；工具仍在可恢复目录：" + destination, failure, restoreFailure); }
                    throw;
                }
                return parent;
            }
            finally { gate.Release(); }
        }, token);

    public Task RestoreAsync(ManagedToolVersion selected, CancellationToken token = default) => Task.Run(async () =>
    {
        await gate.WaitAsync(token);
        try
        {
            EnsureIdle();
            if (!selected.CanRestore || selected.RetirementId is null || !Guid.TryParseExact(selected.RetirementId, "N", out _))
            {
                throw new StudioXException("TOOLS_RETIREMENT", "请选择可恢复区中的开发环境组件版本。");
            }
            var parent = PathBoundary.Resolve(catalog.RootDirectory, ".retired/" + selected.RetirementId);
            var recordPath = PathBoundary.Resolve(parent, "retirement.json");
            var record = await JsonStore.ReadAsync<ToolRetirement>(recordPath, token);
            if (record != new ToolRetirement(1, selected.Id, selected.Version, selected.CompilerId, selected.Fingerprint, record.RetiredUtc))
            {
                throw new StudioXException("TOOLS_CHANGED", "恢复记录发生变化，请刷新。");
            }
            var source = VersionRoot(selected);
            var target = PathBoundary.Resolve(catalog.RootDirectory, selected.Id + "/" + selected.Version);
            if (Directory.Exists(target) || File.Exists(target))
            {
                throw new StudioXException("TOOLS_VERSION_EXISTS", "同一版本已安装，不覆盖现有内容。");
            }
            using var lease = ToolUsageLease.Acquire(target, maintenance: true);
            CheckStamp(source, selected, token);
            var verified = await new ToolsetCatalog(parent).ResolveAsync(selected.Id, selected.Version, selected.CompilerId, token, forceVerification: true, allowDisabled: true);
            if (verified.Fingerprint != selected.Fingerprint)
            {
                throw new StudioXException("TOOLS_CHANGED", "恢复内容的身份发生变化。");
            }
            using var sourceLease = ToolUsageLease.Acquire(source, maintenance: true);
            CheckStamp(source, selected, token);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            token.ThrowIfCancellationRequested();
            Directory.Move(source, target);
            try
            {
                await JsonStore.WriteAsync(recordPath, record with
                {
                    State = "restored"
                }, token);
            }
            catch (Exception failure)
            {
                try
                {
                    Directory.Move(target, source);
                }
                catch (Exception restoreFailure) { throw new AggregateException("恢复记录写入失败，回退也失败。", failure, restoreFailure); }
                throw;
            }
        }
        finally { gate.Release(); }
    }, token);

    public Task PurgeAsync(ManagedToolVersion selected, string? currentProject, CancellationToken token = default)
        => PurgeCoreAsync(selected, currentProject, reviewedRemoval: false, token);

    /// <summary>显式删除先移至可恢复区再删除；中途失败的目录可从管理页恢复或重试。</summary>
    public async Task DeleteAsync(ManagedToolVersion selected, string? currentProject, CancellationToken token = default)
    {
        if (selected.Installed)
        {
            var parent = await RemoveAsync(selected, currentProject, token);
            var report = await InspectAsync(currentProject, token: token);
            var retired = report.Versions.SingleOrDefault(version => version.RetirementId == Path.GetFileName(parent));
            if (retired is null)
            {
                throw new StudioXException("TOOLS_CHANGED", "组件已移至可恢复区，请刷新后重试删除。");
            }
            RequireReviewedDependencies(selected, retired);
            selected = retired;
        }
        await PurgeCoreAsync(selected, currentProject, reviewedRemoval: true, token);
    }

    private Task PurgeCoreAsync(ManagedToolVersion selected, string? currentProject, bool reviewedRemoval, CancellationToken token) => Task.Run(async () =>
    {
        await gate.WaitAsync(token);
        try
        {
            EnsureIdle();
            var fresh = await FindFreshAsync(selected, currentProject, token);
            if (fresh.Installed || !(reviewedRemoval ? fresh.CanDelete : fresh.CanPurge) || fresh.RetirementId is null)
            {
                throw new StudioXException("TOOLS_PROTECTED", "可恢复版本正在占用或检查未完成；常规清理还会保留被工程需要的版本。");
            }
            if (reviewedRemoval)
            {
                RequireReviewedDependencies(selected, fresh);
            }
            var original = PathBoundary.Resolve(catalog.RootDirectory, fresh.Id + "/" + fresh.Version);
            using var lease = ToolUsageLease.Acquire(original, maintenance: true);
            var parent = PathBoundary.Resolve(catalog.RootDirectory, ".retired/" + fresh.RetirementId);
            var root = VersionRoot(fresh);
            using var sourceLease = ToolUsageLease.Acquire(root, maintenance: true);
            if (fresh.RetirementState == "purging" && !Directory.Exists(root))
            {
                if (fresh.TreeStamp != "empty")
                {
                    throw new StudioXException("TOOLS_CHANGED", "剩余目录发生变化。");
                }
            }
            else
            {
                CheckStamp(root, fresh, token);
            }
            _ = CaptureTree(parent, token);
            foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
            {
                if (Path.GetFileName(entry) != "retirement.json" && Path.GetFileName(entry) != fresh.Id)
                {
                    throw new StudioXException("TOOLS_RETIREMENT", "可恢复目录出现额外内容，请核对原始目录后刷新。");
                }
            }
            var idDirectory = Path.GetDirectoryName(root)!;
            if (Directory.Exists(idDirectory) && Directory.EnumerateFileSystemEntries(idDirectory).Any(entry => Path.GetFileName(entry) != fresh.Version))
            {
                throw new StudioXException("TOOLS_RETIREMENT", "可恢复目录存在其他版本，拒绝删除。");
            }
            // parent 只能来自重新读取的 GUID 恢复记录，且整棵树已拒绝重解析点。
            // 删除开始后不响应取消，避免主动留下半个版本；失败保留完整原始异常。
            token.ThrowIfCancellationRequested();
            var recordPath = PathBoundary.Resolve(parent, "retirement.json");
            var record = await JsonStore.ReadAsync<ToolRetirement>(recordPath, token);
            await JsonStore.WriteAsync(recordPath, record with
            {
                State = "purging"
            }, token);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
            if (Directory.Exists(idDirectory))
            {
                Directory.Delete(idDirectory);
            }
            File.Delete(recordPath);
            Directory.Delete(parent);
        }
        finally { gate.Release(); }
    }, token);
    private static void RequireReviewedDependencies(ManagedToolVersion selected, ManagedToolVersion fresh)
    {
        if (!selected.References.Order(StringComparer.Ordinal).SequenceEqual(fresh.References.Order(StringComparer.Ordinal)))
        {
            throw new StudioXException("TOOLS_CHANGED", "组件依赖在确认后发生变化，请刷新并重新审阅影响。");
        }
    }
    private async Task<ManagedToolVersion> FindFreshAsync(ManagedToolVersion selected, string? currentProject, CancellationToken token)
    {
        var report = await InspectCoreAsync(currentProject, null, token);
        var fresh = report.Versions.SingleOrDefault(version => version.Id == selected.Id && version.Version == selected.Version && version.RetirementId == selected.RetirementId);
        if (fresh is null || fresh.Fingerprint != selected.Fingerprint || fresh.TreeStamp != selected.TreeStamp)
        {
            throw new StudioXException("TOOLS_CHANGED", "开发环境组件版本或文件在预览后发生变化，请刷新后重试。");
        }
        return fresh;
    }
    private static void CheckStamp(string root, ManagedToolVersion selected, CancellationToken token)
    {
        if (CaptureTree(root, token).Stamp != selected.TreeStamp)
        {
            throw new StudioXException("TOOLS_CHANGED", "工具目录在预览后发生变化。");
        }
    }
}
