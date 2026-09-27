namespace StudioX.Engine;

public sealed record DownloadPreview(DownloadConfiguration Configuration, DownloadOptions Options,
    string SourceImage, string Format, string Sha256, long ImageBytes);
