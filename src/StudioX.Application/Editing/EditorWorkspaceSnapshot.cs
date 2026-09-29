namespace StudioX.Application.Editing;

public sealed record EditorWorkspaceSnapshot(int FormatVersion, string? Project, string? ActivePath,
    IReadOnlyList<StoredEditorDocument> Documents, DateTimeOffset UpdatedUtc);
