namespace StudioX.Application.CodeIntelligence;

/// <summary>分析配置的证据与处理建议；不把索引故障改写成固件构建失败。</summary>
public sealed record AnalysisEnvironmentIssue(string Code, string Title, string Detail, string RawDiagnostic,
    bool NeedsCacheRepair = false, bool NeedsToolRepair = false);
