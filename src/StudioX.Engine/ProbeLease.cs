namespace StudioX.Engine;

using StudioX.Foundation;

/// <summary>下载与调试共用进程间互斥；文件句柄随进程退出释放，不遗留逻辑锁。</summary>
public sealed class ProbeLease : IDisposable
{
    private readonly FileStream stream;
    private ProbeLease(FileStream stream) => this.stream = stream;
    public static ProbeLease Acquire()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCUStudioX", "locks");
        Directory.CreateDirectory(directory);
        try { return new(new FileStream(Path.Combine(directory, "debug-probe.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
        catch (IOException ex) { throw new StudioXException("PROBE_BUSY", "烧录器正被另一个 StudioX 下载或调试会话占用。", ex); }
    }
    public void Dispose() => stream.Dispose();
}
