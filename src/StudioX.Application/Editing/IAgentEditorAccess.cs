namespace StudioX.Application.Editing;

/// <summary>宿主通过文档事务提供编辑访问；应用层不依赖 WPF。</summary>
public interface IAgentEditorAccess
{
    Task<AgentEditorSnapshot> SnapshotAsync(CancellationToken token);
    Task PresentAsync(AgentEditPlan plan, CancellationToken token);
    Task<IReadOnlyList<WorkspaceFileChange>> SelectedAsync(AgentEditPlan plan, CancellationToken token);
    Task ApplyAsync(IReadOnlyList<WorkspaceFileChange> changes, CancellationToken token);
    Task SaveAsync(IReadOnlyList<WorkspaceBufferSnapshot> snapshots, CancellationToken token);
}
