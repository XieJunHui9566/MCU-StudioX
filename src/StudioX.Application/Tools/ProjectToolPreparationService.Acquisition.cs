namespace StudioX.Application.Tools;

using StudioX.Application.Distribution;
using StudioX.Foundation;

public sealed partial class ProjectToolPreparationService
{
    /// <summary>一次明确操作获取所有缺失的精确版本。固定可信来源，不沿用自定义目录或密钥。</summary>
    public async Task<ProjectToolAcquisitionResult> AcquireFromGithubAsync(ProjectToolPlan snapshot, DistributionService distribution,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        var plan = await RefreshSnapshotAsync(snapshot, null, token);
        var blocked = plan.Requirements.Where(r => r.State is ProjectToolState.Disabled or ProjectToolState.RepairNeeded).ToArray();
        if (blocked.Length != 0)
            throw new StudioXException("TOOLS_PREPARATION_BLOCKED", "请先在开发环境组件管理中启用被禁用组件，或使用组件校验与修复：\n"
                + string.Join('\n', blocked.Select(r => $"{r.Id}/{r.Version}：{r.StatusText}")));
        if (plan.Requirements.All(r => r.State == ProjectToolState.Installed))
            return new(plan, null, []);

        progress?.Report("正在读取 GitHub 组件目录并验证 IDE 内置发布者签名…");
        var listing = await distribution.ReadTrustedAsync(token);
        plan = await RefreshSnapshotAsync(snapshot, listing, token);
        if (plan.Requirements.Any(r => r.State is ProjectToolState.Disabled or ProjectToolState.RepairNeeded))
            throw new StudioXException("TOOLS_PREPARATION_BLOCKED", "读取目录期间组件状态发生变化，请刷新并先启用或修复已有组件。");
        var missing = plan.Requirements.Where(r => r.State == ProjectToolState.Missing).ToArray();
        if (missing.Length == 0) return new(plan, listing, []);
        if (missing.Length > 32) throw new StudioXException("TOOLS_SELECTION", "工程缺失组件过多，请分批手动导入。");
        var unavailable = missing.Where(r => r.Entry is null).ToArray();
        if (unavailable.Length != 0)
            throw new StudioXException("TOOLS_CATALOG_UNAVAILABLE", "GitHub 目录尚未提供以下工程指定版本，本次未安装任何组件：\n"
                + string.Join('\n', unavailable.Select(r => $"{r.Id}/{r.Version} · {r.CompilerId}"))
                + "\n可手动导入相同身份的本地 .mcutoolchain，或在组件库查看已发布版本。工程不会自动改用其他版本。");

        // 同盘下载缓存和所有安装暂存一起计入预算，不能逐包低估多组件工程空间。
        CheckSpace(missing.SelectMany(r => distribution.SpacePlan(r.Entry!, catalog.RootDirectory)));
        var previews = new List<ToolArchivePreview>();
        for (var index = 0; index < missing.Length; index++)
        {
            var requirement = missing[index];
            await EnsureCurrentAsync(plan, requirement, token);
            progress?.Report($"获取 {index + 1}/{missing.Length}：{requirement.Id}/{requirement.Version}");
            var archive = await distribution.DownloadAsync(listing, requirement.Entry!, progress, token);
            previews.Add(await PreviewAsync(plan, requirement, archive, progress, token));
        }
        return await InstallPreparedAsync(plan, previews, listing, progress, token);
    }

    /// <summary>离线批量预览只匹配真实工程缺失需求；所有身份通过后才允许进入安装。</summary>
    public async Task<IReadOnlyList<ToolArchivePreview>> PreviewManualAsync(ProjectToolPlan snapshot, IReadOnlyList<string> archives,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        var plan = await RefreshSnapshotAsync(snapshot, null, token);
        if (archives.Count is < 1 or > 32) throw new StudioXException("TOOLS_SELECTION", "一次请选择 1 至 32 个开发环境组件归档。");
        var previews = new List<ToolArchivePreview>();
        foreach (var archive in archives)
        {
            var preview = await management.PreviewInstallAsync(archive, progress, token);
            var requirement = MatchMissing(plan, preview);
            if (previews.Any(p => p.Id == preview.Id)) throw new StudioXException("TOOLS_SELECTION", "重复选择同一开发环境组件，未安装：" + preview.Id);
            await EnsureCurrentAsync(plan, requirement, token);
            EnsureIdentity(requirement, preview, verifyCatalog: false);
            previews.Add(preview);
        }
        CheckSpace(previews.SelectMany(InstallSpacePlan));
        return previews;
    }

    /// <summary>每个组件独立暂存并原子发布。取消保留已完成组件和下载缓存；未完成组件不发布。</summary>
    public async Task<ProjectToolAcquisitionResult> InstallManualAsync(ProjectToolPlan snapshot, IReadOnlyList<ToolArchivePreview> previews,
        IProgress<string>? progress = null, CancellationToken token = default)
        => await InstallPreparedAsync(await RefreshSnapshotAsync(snapshot, null, token), previews, null, progress, token);

    private async Task<ProjectToolAcquisitionResult> InstallPreparedAsync(ProjectToolPlan plan, IReadOnlyList<ToolArchivePreview> previews,
        DistributionListing? listing, IProgress<string>? progress, CancellationToken token)
    {
        if (previews.Count is < 1 or > 32 || previews.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != previews.Count)
            throw new StudioXException("TOOLS_SELECTION", "请选择不重复的工程开发环境组件。");
        // 先绑定整批身份；调用方不能用伪造预览绕过需求、内容锁或目录摘要。
        foreach (var preview in previews) EnsureIdentity(MatchMissing(plan, preview), preview, listing is not null);
        CheckSpace(previews.SelectMany(InstallSpacePlan));
        var installed = new List<DevelopmentComponentInstallResult>();
        foreach (var preview in previews)
        {
            var requirement = MatchMissing(plan, preview);
            progress?.Report($"安装 {installed.Count + 1}/{previews.Count}：{preview.Id}/{preview.Version}");
            await InstallAsync(plan, requirement, preview, progress, token, verifyCatalog: listing is not null);
            installed.Add(new(preview.Identity, preview.Fingerprint, false));
            progress?.Report($"已安装 {preview.Id}/{preview.Version}");
        }
        var current = await RefreshSnapshotAsync(plan, listing, token);
        return new(current, listing, installed);
    }

    private async Task<ProjectToolPlan> RefreshSnapshotAsync(ProjectToolPlan snapshot, DistributionListing? listing, CancellationToken token)
    {
        if (snapshot.ProjectDirectory is null) throw new StudioXException("TOOLS_SELECTION", "请先选择或创建工程，也可在组件库选择独立组件。");
        var current = await InspectAsync(snapshot.ProjectDirectory, listing, token);
        if (current.Fingerprint != snapshot.Fingerprint) throw new StudioXException("TOOLS_PROJECT_CHANGED", "工程配置发生变化，请刷新后重试。");
        return current;
    }

    private static ProjectToolRequirement MatchMissing(ProjectToolPlan plan, ToolArchivePreview preview)
        => plan.Requirements.SingleOrDefault(r => r.Id == preview.Id && r.Version == preview.Version && r.CompilerId == preview.CompilerId
            && r.State == ProjectToolState.Missing)
            ?? throw new StudioXException("TOOLS_PROJECT_IDENTITY", $"组件 {preview.Id}/{preview.Version} · {preview.CompilerId} 不属于此工程缺失的精确需求。已有组件请使用管理或修复入口。");

    private static void CheckSpace(IEnumerable<DistributionSpacePlan> plans)
    {
        foreach (var volume in plans.GroupBy(p => p.Directory, StringComparer.OrdinalIgnoreCase))
            if (volume.Sum(p => p.RequiredBytes) > volume.Min(p => p.AvailableBytes))
                throw new StudioXException("INSTALL_SPACE", "组件缓存和安装暂存所需磁盘空间不足：" + volume.Key);
    }
}
