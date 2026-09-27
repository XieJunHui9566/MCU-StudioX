namespace StudioX.Engine.Lvgl;

public sealed record LvglUiInspection(string Project, string LibraryDirectory, string UiDirectory,
    IReadOnlyList<string> SourceFiles, IReadOnlyList<string> IncludeDirectories, IReadOnlyList<string> ConfigurationHeaders,
    IReadOnlyList<LvglUiEntryPoint> EntryPoints, IReadOnlyList<string> ResourceDirectories,
    IReadOnlyList<LvglDiagnostic> Diagnostics);
