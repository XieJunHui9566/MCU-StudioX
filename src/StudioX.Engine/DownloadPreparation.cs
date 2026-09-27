namespace StudioX.Engine;

public sealed record DownloadPreparation(DownloadConfiguration Configuration, DownloadOptions Options,
    ResolvedToolset Tools, string SourceImage, string Image, string[] Arguments, string LogPath)
{
    public ulong ImageByteCount
    {
        get; init;
    }
}
