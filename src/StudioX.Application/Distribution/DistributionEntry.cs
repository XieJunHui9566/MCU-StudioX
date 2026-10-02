namespace StudioX.Application.Distribution;

public sealed record DistributionEntry(string Kind, string Id, string Version, string Name, string Archive,
    string Sha256, long DownloadBytes, long InstalledBytes, string License, string SourceUrl,
    string ReleaseNotes, int PluginApi = 0, string[]? Frameworks = null);
