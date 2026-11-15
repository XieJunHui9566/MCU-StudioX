namespace StudioX.Engine;

using System.Text.Json;
using StudioX.Foundation;

/// <summary>将公共组件校验结果交给同次 SDK 子进程；文件元数据变化时 SDK 重新读取内容。</summary>
internal sealed class Mx251VerifiedTools : IDisposable
{
    internal const string EnvironmentKey = "MX251_VERIFIED_TOOLSET";
    private readonly FileStream lease;
    internal string Path
    {
        get;
    }

    internal Mx251VerifiedTools(string root, ResolvedToolset tools)
    {
        Path = PathBoundary.Resolve(root, ".build/mx251/verified-tools.json");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        lease = new FileStream(Path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        try
        {
            JsonSerializer.Serialize(lease, new
            {
                schema = 1,
                root = tools.RootDirectory,
                files = tools.VerifiedFiles
            }, JsonStore.Options);
            lease.Flush();
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        lease.Dispose();
        File.Delete(Path);
    }
}
