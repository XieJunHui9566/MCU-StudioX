namespace StudioX.Engine.Zephyr;

using System.Security.Cryptography;
using System.Text;
using StudioX.Foundation;

/// <summary>对 Zephyr 的 DTS、Kconfig、源文件和资源统一取快照，避免只检查 C 文件漏掉板级变化。</summary>
internal static class ZephyrSourceSnapshot
{
    internal static IEnumerable<string> Files(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.Ordinal))
            {
                if (directory == root && item.Name is ".build" or "build" or ".git" or ".studiox")
                {
                    continue;
                }
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new StudioXException("ZEPHYR_SOURCE_LINK", "Zephyr 工程快照不接受目录链接或文件链接：" + item.FullName);
                }
                if (item is DirectoryInfo)
                {
                    pending.Push(item.FullName);
                }
                else
                {
                    yield return Path.GetRelativePath(root, item.FullName).Replace('\\', '/');
                }
            }
        }
    }

    internal static async Task<string> HashAsync(string root, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relative in Files(root).Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));
            await using var stream = File.OpenRead(PathBoundary.Resolve(root, relative));
            hash.AppendData(await SHA256.HashDataAsync(stream, token));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
