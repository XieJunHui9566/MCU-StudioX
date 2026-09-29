namespace StudioX.Application.Editing;

public sealed record RecoveredEditorDocument(SourceDocument Source, string Text, StoredEditorDocument View, string? Notice);
