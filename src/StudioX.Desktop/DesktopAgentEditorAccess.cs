namespace StudioX.Desktop;

using StudioX.Application.Editing;

internal sealed class DesktopAgentEditorAccess(
    Func<CancellationToken, Task<AgentEditorSnapshot>> snapshot,
    Func<AgentEditPlan, CancellationToken, Task> present,
    Func<AgentEditPlan, CancellationToken, Task<IReadOnlyList<WorkspaceFileChange>>> selected,
    Func<IReadOnlyList<WorkspaceFileChange>, CancellationToken, Task> apply,
    Func<IReadOnlyList<WorkspaceBufferSnapshot>, CancellationToken, Task> save) : IAgentEditorAccess
{
    public Task<AgentEditorSnapshot> SnapshotAsync(CancellationToken token) => snapshot(token);
    public Task PresentAsync(AgentEditPlan plan, CancellationToken token) => present(plan, token);
    public Task<IReadOnlyList<WorkspaceFileChange>> SelectedAsync(AgentEditPlan plan, CancellationToken token) => selected(plan, token);
    public Task ApplyAsync(IReadOnlyList<WorkspaceFileChange> changes, CancellationToken token) => apply(changes, token);
    public Task SaveAsync(IReadOnlyList<WorkspaceBufferSnapshot> snapshots, CancellationToken token) => save(snapshots, token);
}
