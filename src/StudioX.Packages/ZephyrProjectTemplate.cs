namespace StudioX.Packages;

/// <summary>目标工程相对路径到包内模板资源的显式映射。</summary>
public sealed record ZephyrProjectTemplate(
    string Id,
    string DisplayName,
    string Description,
    IReadOnlyDictionary<string, string> Files);
