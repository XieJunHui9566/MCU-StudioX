namespace StudioX.Engine;

public sealed record BuildDetails(ProjectManifest Project, string SourceStamp, string ImageFingerprint,
    IReadOnlyList<BuildContribution> Contributions, IReadOnlyList<CompilationTiming> Timings);
