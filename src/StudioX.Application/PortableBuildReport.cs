namespace StudioX.Application;

/// <summary>跨平台桌面构建结果，保留原始诊断和产物位置。</summary>
public sealed record PortableBuildReport(bool Success, string Summary, string Log, string? LogPath, IReadOnlyList<string> Artifacts);
