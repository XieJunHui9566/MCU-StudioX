namespace StudioX.Engine.Arduino;

using System.Security.Cryptography;
using System.Text;
using StudioX.Foundation;

/// <summary>遍历 sketch 的实际输入，拒绝链接；构建前后与上传前共用同一内容摘要。</summary>
public static class ArduinoSourceSnapshot
{
    public static IEnumerable<string> Files(string root, string relative = "")
    {
        var directory = relative.Length == 0 ? Path.GetFullPath(root) : PathBoundary.Resolve(root, relative);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new StudioXException("PATH_LINK", "Arduino 工程或依赖不能包含目录链接。");
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(entry);
            if (relative.Length == 0 && name is ".git" or ".studiox" or ".build")
            {
                continue;
            }
            var child = Path.GetRelativePath(root, entry).Replace('\\', '/');
            _ = PathBoundary.Resolve(root, child);
            if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
            {
                foreach (var file in Files(root, child))
                {
                    yield return file;
                }
            }
            else
            {
                yield return child;
            }
        }
    }

    public static async Task<string> HashAsync(string root, bool includeProject, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = Files(root).ToList();
        if (includeProject)
        {
            files.Add(".studiox/project.json");
        }
        foreach (var relative in files.Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));
            await using var stream = File.OpenRead(PathBoundary.Resolve(root, relative));
            hash.AppendData(await SHA256.HashDataAsync(stream, token));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
