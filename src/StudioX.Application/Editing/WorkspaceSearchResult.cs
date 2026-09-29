namespace StudioX.Application.Editing;

public sealed record WorkspaceSearchResult(IReadOnlyList<WorkspaceFileChange> Files,
    IReadOnlyList<string> Notices, int ScannedFiles);
