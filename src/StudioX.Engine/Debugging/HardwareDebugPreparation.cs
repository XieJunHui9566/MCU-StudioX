namespace StudioX.Engine.Debugging;

public sealed record HardwareDebugPreparation(string ProjectDirectory, DownloadConfiguration Configuration, ResolvedToolset Tools, string Elf, string LogPath)
{
    /// <summary>仅调用者显式选择时进行复位连接；常规附加不自动复位。</summary>
    public bool ConnectUnderReset { get; init; }
    public IProgress<DebugStartupProgress>? Progress { get; init; }
    public DownloadImageSnapshot? PinMapping { get; init; }
    public ulong ImageByteCount
    {
        get; init;
    }
}
