namespace StudioX.Application.Editing;

/// <summary>按项目与文件隔离的本地文本版本，不依赖 Git 提交。</summary>
public sealed record LocalHistoryEntry(string Id, string Path, DateTimeOffset CreatedUtc, string Reason, string Hash, string Text);
