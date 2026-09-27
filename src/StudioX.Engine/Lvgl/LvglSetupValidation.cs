namespace StudioX.Engine.Lvgl;

public sealed record LvglSetupValidation(bool IsValid, LvglLibraryCandidate? Library, IReadOnlyList<LvglDiagnostic> Diagnostics);
