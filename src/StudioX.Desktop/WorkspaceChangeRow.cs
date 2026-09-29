namespace StudioX.Desktop;

using StudioX.Application.Editing;

internal sealed class WorkspaceChangeRow(WorkspaceFileChange change)
{
    public WorkspaceFileChange Change { get; } = change;
    public bool Selected { get; set; } = change.CanApply;
    public string Path => Change.Path;
    public int Count => Change.Matches.Count;
    public string State => Change.Source.IsReadOnly ? "只读 · 不会修改" : Change.WasOpen ? "使用编辑缓冲区" : "使用磁盘快照";
}
