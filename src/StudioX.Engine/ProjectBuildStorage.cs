namespace StudioX.Engine;

using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>日常工程输出只保留 latest；回读备份与尚未确认恢复的调试会话分开保管。</summary>
public sealed class ProjectBuildStorage : IDisposable
{
    private readonly string root, family;
    private readonly FileStream lease;
    private bool disposed;

    private ProjectBuildStorage(string root, string family, FileStream lease)
    {
        this.root = root;
        this.family = family;
        this.lease = lease;
    }

    public static ProjectBuildStorage Acquire(string root, string family)
    {
        if (family is not ("pic" or "arduino" or "ag32-logic" or "stc-isp"))
        {
            throw new ArgumentException("Unknown output family.", nameof(family));
        }
        root = Path.GetFullPath(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new StudioXException("PATH_LINK", "工程根目录不能是链接。");
        }
        var path = PathBoundary.Resolve(root, ".studiox/locks/" + family + "-artifacts.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            return new(root, family, new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException error) { throw new StudioXException("PROJECT_OUTPUT_BUSY", "此工程正在构建、下载或调试，请等待当前操作结束。", error); }
    }

    public string Prepare(string area)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (area switch
        {
            "pic" or "pic-download" or "pic-debug" => "pic",
            "arduino" or "arduino-upload" => "arduino",
            "ag32-logic" => "ag32-logic",
            "stc-monitor" or "stc-usb" => "stc-isp",
            _ => ""
        } != family)
        {
            throw new ArgumentException("Output area does not belong to this lease.", nameof(area));
        }
        var parent = PathBoundary.Resolve(root, ".build/" + area);
        Directory.CreateDirectory(parent);
        var latest = PathBoundary.Resolve(parent, "latest");
        var targets = Directory.EnumerateDirectories(parent).Where(path => Path.GetFileName(path) == "latest" ||
            Regex.IsMatch(Path.GetFileName(path), @"\A[0-9a-fA-F]{32}\z")).ToArray();
        // 全部预检成功后才修改文件；未知名称的用户目录不属于清理范围。
        foreach (var path in targets)
        {
            ValidateTree(path);
        }
        var recovery = PathBoundary.Resolve(parent, "recovery");
        var protectedRuns = area == "pic-debug" ? targets.Where(NeedsRecovery).ToArray() : [];
        if (protectedRuns.Length > 1 || protectedRuns.Length > 0 && Directory.Exists(recovery))
        {
            throw new StudioXException("PIC_RECOVERY_REQUIRED", "存在尚未确认恢复的 PIC 调试备份，已保留原目录；请先处理恢复记录：" + parent);
        }
        if (area is "pic-download" or "pic-debug")
        {
            var previous = targets.Except(protectedRuns).Select(path => Path.Combine(path, "before.hex"))
                .Where(File.Exists).OrderByDescending(path => File.GetLastWriteTimeUtc(path)).FirstOrDefault();
            if (previous is not null)
            {
                PreserveBackup(parent, Path.GetDirectoryName(previous)!);
            }
        }
        if (protectedRuns.Length == 1)
        {
            Directory.Move(protectedRuns[0], recovery);
        }
        foreach (var path in targets.Except(protectedRuns))
        {
            Directory.Delete(path, recursive: true);
        }
        Directory.CreateDirectory(latest);
        return latest;
    }

    private static bool NeedsRecovery(string directory)
    {
        if (!File.Exists(Path.Combine(directory, "before.hex")))
        {
            return false;
        }
        var record = Path.Combine(directory, "restored.json");
        if (!File.Exists(record))
        {
            return true;
        }
        if (new FileInfo(record).Length > 65536)
        {
            throw new StudioXException("PIC_RECOVERY_REQUIRED", "调试恢复记录过大，原备份未清理：" + record);
        }
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(record));
            return !json.RootElement.TryGetProperty("verified", out var verified) || verified.ValueKind != JsonValueKind.True;
        }
        catch (JsonException error) { throw new StudioXException("PIC_RECOVERY_REQUIRED", "调试恢复记录无效，原备份未清理：" + record, error); }
    }

    private static void PreserveBackup(string parent, string source)
    {
        var backup = PathBoundary.Resolve(parent, "backup");
        var staging = PathBoundary.Resolve(parent, ".backup-new");
        if (Directory.Exists(backup))
        {
            ValidateTree(backup);
        }
        if (Directory.Exists(staging))
        {
            ValidateTree(staging);
            Directory.Delete(staging, true);
        }
        Directory.CreateDirectory(staging);
        foreach (var name in new[] { "before.hex", "after.hex", "result.json", "restored.json", "download.log", "programming.log", "debug.log" })
        {
            var file = Path.Combine(source, name);
            if (File.Exists(file))
            {
                File.Copy(file, Path.Combine(staging, name));
            }
        }
        File.WriteAllText(Path.Combine(staging, "backup-info.json"), JsonSerializer.Serialize(new
        {
            sourceRun = Path.GetFileName(source),
            savedUtc = DateTimeOffset.UtcNow
        }, JsonStore.Options));
        if (Directory.Exists(backup))
        {
            Directory.Delete(backup, true);
        }
        Directory.Move(staging, backup);
    }

    private static void ValidateTree(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("PATH_LINK", "输出目录包含链接，未清理：" + current);
            }
            foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new StudioXException("PATH_LINK", "输出目录包含链接，未清理：" + entry.FullName);
                }
                if (entry is DirectoryInfo)
                {
                    pending.Push(entry.FullName);
                }
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        lease.Dispose();
    }
}
