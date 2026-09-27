namespace StudioX.Engine.Lvgl;

public sealed record LvglLibraryCandidate(string Directory, string? Version, bool IsSupported, bool IsComplete,
    string HeaderSha256, string? SourceFingerprint, IReadOnlyList<string> MissingFiles, IReadOnlyList<LvglDiagnostic> Diagnostics);
