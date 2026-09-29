namespace StudioX.Application.Editing;

public sealed class AgentEditPlan(string id, string taskId, string reason, IReadOnlyList<WorkspaceFileChange> changes, bool isAtomic = false)
{
    public string Id { get; } = id;
    public string TaskId { get; } = taskId;
    public string Reason { get; } = reason;
    public bool IsAtomic { get; } = isAtomic;
    public IReadOnlyList<WorkspaceFileChange> Changes { get; internal set; } = changes;
    public string Status { get; internal set; } = "pending";
    public DateTimeOffset CreatedUtc { get; } = DateTimeOffset.UtcNow;
}
