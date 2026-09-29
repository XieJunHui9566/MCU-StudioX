namespace StudioX.Application.Editing;

public sealed record StoredEditorDocument(string Path, string? Draft, string? Baseline, string DiskHash,
    int CodePage, bool HasBom, bool ReadOnly, int Caret, int SelectionStart, int SelectionLength, double Vertical, double Horizontal, int Group = 0, bool Selected = false);
