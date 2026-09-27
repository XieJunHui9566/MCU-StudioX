namespace StudioX.Desktop;

/// <summary>按 git log 输出顺序排列的一个提交及其父提交。</summary>
public sealed record GitGraphCommit(string Hash, IReadOnlyList<string> Parents);
