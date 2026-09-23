namespace StudioX.Packages;

public sealed record InstalledPack(PackManifest Manifest, string RootDirectory, string ContentHash);
