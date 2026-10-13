namespace StudioX.Application.Editing;

/// <summary>工程相对路径变化；目录改名同时影响其下的打开文档。</summary>
public sealed record ProjectFileChange(string Path, ProjectFileChangeKind Kind = ProjectFileChangeKind.Changed, string? PreviousPath = null);
