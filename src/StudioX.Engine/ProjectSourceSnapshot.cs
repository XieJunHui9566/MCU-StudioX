namespace StudioX.Engine;

using System.Security.Cryptography;
using System.Text;
using StudioX.Foundation;

/// <summary>独立构建后端的工程输入快照；排除 IDE/Git 目录并拒绝链接。</summary>
internal static class ProjectSourceSnapshot
{
    public static IEnumerable<string> Files(string root, string relative = "")
    {
        var directory = relative.Length == 0 ? Path.GetFullPath(root) : PathBoundary.Resolve(root, relative);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new StudioXException("PATH_LINK", "构建输入不接受目录链接。");
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
            if (Directory.Exists(entry))
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

    public static async Task<string> HashAsync(string root, bool project, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = Files(root).ToList();
        if (project)
        {
            files.Add(".studiox/project.json");
        }
        foreach (var relative in files.Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));
            await using var input = File.OpenRead(PathBoundary.Resolve(root, relative));
            hash.AppendData(await SHA256.HashDataAsync(input, token));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static async Task CopyAsync(string source, string target, CancellationToken token)
    {
        Directory.CreateDirectory(target);
        foreach (var relative in Files(source))
        {
            var destination = PathBoundary.Resolve(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = File.OpenRead(PathBoundary.Resolve(source, relative));
            await using var output = File.Create(destination);
            await input.CopyToAsync(output, token);
        }
    }
}
