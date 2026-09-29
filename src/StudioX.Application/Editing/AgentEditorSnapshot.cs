namespace StudioX.Application.Editing;

public sealed record AgentEditorSnapshot(string? ActivePath, int Caret, int SelectionStart, int SelectionLength,
    IReadOnlyList<WorkspaceBufferSnapshot> Documents);
