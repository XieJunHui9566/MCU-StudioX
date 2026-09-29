namespace StudioX.Application.Editing;

/// <summary>预览与应用共用同一文本快照；不在预览阶段写入磁盘。</summary>
public sealed record WorkspaceFileChange(SourceDocument Source, string Before, string After,
    IReadOnlyList<TextSearchMatch> Matches, bool WasOpen)
{
    public string Path => Source.RelativePath;
    public bool CanApply => !Source.IsReadOnly && Before != After;
}
