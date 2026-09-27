namespace StudioX.Engine.Lvgl;

public sealed record LvglDiscoveryReport(string Project, IReadOnlyList<LvglLibraryCandidate> Candidates,
    IReadOnlyList<LvglDiagnostic> Diagnostics);
