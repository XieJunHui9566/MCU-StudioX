namespace StudioX.Application.Tools;

using StudioX.Application.Distribution;

public enum ProjectToolState { Missing, Installed, RepairNeeded }

public sealed record ProjectToolRequirement(string Id, string Version, string CompilerId, string Purpose,
    ProjectToolState State, string Diagnostic, string? LockedFingerprint, DistributionEntry? Entry)
{
    public string StatusText => State switch { ProjectToolState.Missing => "未安装", ProjectToolState.Installed => "入口检查通过", _ => "需要修复" };
    public string DownloadText => Entry is null ? "目录未提供此版本" : Size(Entry.DownloadBytes);
    public string InstalledText => Entry is null ? "—" : Size(Entry.InstalledBytes);
    private static string Size(long bytes) => bytes < 1024 ? $"{bytes:N0} B" : bytes < 1048576 ? $"{bytes / 1024d:N1} KiB"
        : bytes < 1073741824 ? $"{bytes / 1048576d:N1} MiB" : $"{bytes / 1073741824d:N2} GiB";
}

/// <summary>工程配置快照只用于检查与安装绑定；不会替工程建立或改写内容锁。</summary>
public sealed record ProjectToolPlan(string? ProjectDirectory, string ProjectName, string Fingerprint,
    string Message, IReadOnlyList<ProjectToolRequirement> Requirements);
