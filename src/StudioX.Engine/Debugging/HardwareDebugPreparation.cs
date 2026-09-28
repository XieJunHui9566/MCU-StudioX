namespace StudioX.Engine.Debugging;

public sealed record HardwareDebugPreparation(string ProjectDirectory, DownloadConfiguration Configuration, ResolvedToolset Tools, string Elf, string LogPath)
{
    public DownloadImageSnapshot? PinMapping { get; init; }
    public ulong ImageByteCount
    {
        get; init;
    }
}
