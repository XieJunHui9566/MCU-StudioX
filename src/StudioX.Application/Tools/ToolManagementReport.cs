namespace StudioX.Application.Tools;

public sealed record ToolManagementReport(DateTimeOffset CheckedUtc, IReadOnlyList<ManagedToolVersion> Versions,
    IReadOnlyList<string> Projects, IReadOnlyList<string> Diagnostics, bool ReferencesComplete)
{
    public IReadOnlyList<ToolRepairRecovery> Recoveries { get; init; } = [];
    public long InstalledBytes => Versions.Where(version => version.Installed).Sum(version => version.Bytes);
    public long RetiredBytes => Versions.Where(version => !version.Installed).Sum(version => version.Bytes);
    public string Summary => $"已安装 {InstalledBytes / 1073741824d:F2} GiB · 可恢复区 {RetiredBytes / 1073741824d:F2} GiB · {Projects.Count} 个登记/最近工程 · "
        + (ReferencesComplete ? "依赖检查完成" : "部分依赖无法核实，暂停清理")
        + (Recoveries.Count > 0 ? $" · {Recoveries.Count} 项未完成安装或修复" : "") + "。统计为逻辑文件大小，硬链接与压缩会影响实际释放空间。";
}
