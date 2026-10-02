namespace StudioX.Application.Components;

public sealed record InstalledComponent(ComponentManifest Manifest, string ArchiveSha256, string RelativeDirectory);
