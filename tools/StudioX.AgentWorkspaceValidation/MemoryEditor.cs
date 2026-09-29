using StudioX.Application;
using StudioX.Application.Editing;

internal sealed class MemoryEditor(WorkbenchService services, string project) : IAgentEditorAccess
{
    public Dictionary<string, WorkspaceBufferSnapshot> Documents { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Func<AgentEditPlan, IReadOnlyList<WorkspaceFileChange>>? Selection
    {
        get; set;
    }
    public Action? BeforeSave
    {
        get; set;
    }
    public Task<AgentEditorSnapshot> SnapshotAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new AgentEditorSnapshot("src/main.c", 0, 0, 4, Documents.Values.ToArray()));
    }
    public Task PresentAsync(AgentEditPlan plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<WorkspaceFileChange>> SelectedAsync(AgentEditPlan plan, CancellationToken token) => Task.FromResult(Selection?.Invoke(plan) ?? plan.Changes);
    public async Task ApplyAsync(IReadOnlyList<WorkspaceFileChange> changes, CancellationToken token)
    {
        await services.WorkspaceEdits.ValidateAsync(project, changes, Documents.Values.ToArray(), token);
        foreach (var change in changes)
        {
            await services.LocalHistory.CaptureAsync(project, change.Path, change.Before, "AI test", token);
            Documents[change.Path] = new(change.Source, change.After);
        }
    }
    public async Task SaveAsync(IReadOnlyList<WorkspaceBufferSnapshot> snapshots, CancellationToken token)
    {
        BeforeSave?.Invoke();
        foreach (var item in snapshots)
        {
            if (Documents[item.Source.RelativePath].Text != item.Text)
            {
                throw new IOException("stale save");
            }
        }
        foreach (var item in snapshots)
        {
            var saved = await services.Files.SaveAsync(project, item.Source, item.Text, token);
            Documents[item.Source.RelativePath] = new(saved, saved.Text);
        }
    }
}
