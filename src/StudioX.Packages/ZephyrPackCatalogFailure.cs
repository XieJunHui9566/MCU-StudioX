namespace StudioX.Packages;

/// <summary>目录项损坏不妨碍使用其他已校验的实验包；保留原始异常供界面诊断。</summary>
public sealed record ZephyrPackCatalogFailure(string Directory, string Diagnostic);
