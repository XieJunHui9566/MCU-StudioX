namespace StudioX.Engine;

/// <summary>Git porcelain v1 的两个状态位原样保留；问号表示未跟踪。</summary>
public sealed record GitWorkingFile(string Path, string? OriginalPath, char IndexStatus, char WorkTreeStatus, bool IsUntracked);
