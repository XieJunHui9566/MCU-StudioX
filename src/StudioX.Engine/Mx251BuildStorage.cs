namespace StudioX.Engine;

using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>普通工程只保留一次构建/下载；跨进程互斥避免清理正在使用的目录。</summary>
internal static class Mx251BuildStorage
{
    internal const string Firmware = ".build/firmware.hex";

    internal static IDisposable Acquire(string root)
    {
        var path = PathBoundary.Resolve(root, ".studiox/locks/mx251-operation.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) { throw new StudioXException("MX251_BUSY", "此工程正在编译、保存频率或下载，请等待当前操作完成。", error); }
    }

    internal static string Prepare(string root, bool download)
    {
        var parent = PathBoundary.Resolve(root, download ? ".build/mx251-isp" : ".build/mx251");
        var latest = PathBoundary.Resolve(parent, "latest");
        Directory.CreateDirectory(parent);
        // 仅识别本后端生成的目录，未知用户文件不自动删除；不跟随目录链接。
        var pattern = download ? "^[0-9a-f]{32}$" : @"^\d{8}-\d{6}-[0-9a-f]{8}$";
        var targets = Directory.EnumerateDirectories(parent).Where(p => Path.GetFileName(p) == "latest" ||
            Regex.IsMatch(Path.GetFileName(p), pattern, RegexOptions.IgnoreCase)).ToArray();
        foreach (var target in targets)
        {
            if (Path.GetDirectoryName(Path.GetFullPath(target)) != parent)
            {
                throw new StudioXException("MX251_CLEANUP", "清理目标越界。");
            }
            var pending = new Stack<string>();
            pending.Push(target);
            while (pending.TryPop(out var directory))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new StudioXException("MX251_CLEANUP", "构建目录中存在链接，请先移除链接后重试：" + directory);
                }
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new StudioXException("MX251_CLEANUP", "构建目录中存在链接，请先移除链接后重试：" + entry.FullName);
                    }
                    if (entry is DirectoryInfo)
                    {
                        pending.Push(entry.FullName);
                    }
                }
            }
        }
        foreach (var target in targets)
        {
            Directory.Delete(target, recursive: true);
        }
        return latest;
    }
}
