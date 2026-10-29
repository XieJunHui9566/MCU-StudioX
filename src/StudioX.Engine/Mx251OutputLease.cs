namespace StudioX.Engine;

using System.Text;
using StudioX.Foundation;

/// <summary>IDE 与独立 CLI 共用输出锁；子构建持有只读租约，父进程退出后仍阻止新写者。</summary>
internal sealed class Mx251OutputLease : IDisposable
{
    internal const string EnvironmentKey = "MX251_OUTPUT_LEASE";
    private readonly FileStream stream;
    internal string Token
    {
        get;
    }
    private Mx251OutputLease(FileStream stream, string token)
    {
        this.stream = stream;
        Token = token;
    }
    internal static Mx251OutputLease Acquire(string root)
    {
        var path = PathBoundary.Resolve(root, ".build/mx251/build.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var token = Guid.NewGuid().ToString("N");
        try
        {
            // 先独占探测：不能把仍在运行的子进程的读锁误当作空闲目录。
            using (var probe = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                probe.SetLength(0);
                probe.Write(Encoding.UTF8.GetBytes(token));
                probe.Flush();
            }
            var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(lease, leaveOpen: true);
            if (reader.ReadToEnd() != token)
            {
                lease.Dispose();
                throw new IOException("Output lease changed");
            }
            return new(lease, token);
        }
        catch (IOException error) { throw new StudioXException("MX251_BUSY", "该工程的 MX251 输出正在被另一构建使用，未更改已有产物。", error); }
    }
    public void Dispose() => stream.Dispose();
}
