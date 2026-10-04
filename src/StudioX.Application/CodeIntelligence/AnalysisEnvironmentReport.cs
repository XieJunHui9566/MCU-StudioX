namespace StudioX.Application.CodeIntelligence;

public sealed record AnalysisEnvironmentReport(bool HasDatabase, int TranslationUnits,
    IReadOnlyList<AnalysisEnvironmentIssue> Issues)
{
    public IReadOnlyList<string> ResponseFiles { get; init; } = [];
    public bool CanUseDatabase => HasDatabase && Issues.Count == 0;
    public string Summary => !HasDatabase ? "尚未生成原生编译数据库，请先配置工程。"
        : Issues.Count == 0 ? $"原生分析配置一致，共 {TranslationUnits:N0} 个 C/C++ 编译单元；不代表固件或目标 ABI 验收。"
        : "分析配置需要刷新：" + Issues[0].Detail;
}
