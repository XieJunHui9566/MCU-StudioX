namespace StudioX.Application.Tools;

/// <summary>工程配置快照只用于检查与安装绑定；不会替工程建立或改写内容锁。</summary>
public sealed record ProjectToolPlan(string? ProjectDirectory, string ProjectName, string Fingerprint,
    string Message, IReadOnlyList<ProjectToolRequirement> Requirements);
