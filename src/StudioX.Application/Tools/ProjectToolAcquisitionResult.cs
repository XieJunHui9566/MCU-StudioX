namespace StudioX.Application.Tools;

using StudioX.Application.Distribution;

/// <summary>获取结果保留每个已安装组件和最新工程状态；不为工程建立新锁。</summary>
public sealed record ProjectToolAcquisitionResult(ProjectToolPlan Plan, DistributionListing? Listing,
    IReadOnlyList<DevelopmentComponentInstallResult> Installed);
