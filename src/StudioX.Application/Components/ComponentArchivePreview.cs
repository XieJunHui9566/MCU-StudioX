namespace StudioX.Application.Components;

public sealed record ComponentArchivePreview(string Archive, string Sha256, long Bytes, ComponentManifest Manifest);
