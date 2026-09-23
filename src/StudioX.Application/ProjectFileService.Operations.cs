namespace StudioX.Application;

using System.Diagnostics;
using StudioX.Foundation;

public sealed partial class ProjectFileService
{
    private static readonly string[] ReservedProjectPaths = [".studiox", ".git", "device/manifest.json", "device/CMakeLists.txt", "device/platform.cmake"];
    public static bool ContainsPath(string parent, string path) => path.Equals(parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
    public static bool CanRenameEntry(string relative) => relative.Length > 0 && !ReservedProjectPaths.Any(path => ContainsPath(path, relative) || ContainsPath(relative, path));
    public static bool CanCreateIn(string relative) => !ReservedProjectPaths.Any(path => ContainsPath(path, relative));
    public static string ParentDirectory(string relative) => relative.LastIndexOf('/') is var slash && slash >= 0 ? relative[..slash] : "";
    public bool FileExists(string project, string relative) => File.Exists(PathBoundary.Resolve(project, relative));

    public string GetEntryPath(string project, string relative)
    {
        var full = relative.Length == 0 ? Path.GetFullPath(project) : PathBoundary.Resolve(project, relative);
        if (!File.Exists(full) && !Directory.Exists(full)) throw new StudioXException("FILE_MISSING", "文件或文件夹已不存在，请刷新工程树。");
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new StudioXException("PATH_LINK", "暂不操作链接文件或目录。");
        return full;
    }

    public static void ValidateEntryName(string name)
    {
        if (name.Contains('/') || name.Contains('\\')) throw new StudioXException("FILE_NAME", "请输入名称，不要包含目录分隔符。");
        _ = PathBoundary.Resolve(Path.GetTempPath(), name);
    }
    private string NewEntryPath(string project, string parent, string name)
    {
        ValidateEntryName(name);
        if (!CanCreateIn(parent)) throw new StudioXException("FILE_MANAGED", "此目录保存工程内部配置，不能在这里新建或粘贴文件。");
        var folder = GetEntryPath(project, parent);
        if (!Directory.Exists(folder)) throw new StudioXException("FILE_PARENT", "目标必须是文件夹。");
        var relative = parent.Length == 0 ? name : parent + "/" + name;
        if (!CanCreateIn(relative)) throw new StudioXException("FILE_MANAGED", "此名称由工程内部配置保留。");
        return PathBoundary.Resolve(project, relative);
    }
    public async Task<string> CreateEntryAsync(string project, string parent, string name, bool directory, CancellationToken token = default)
    {
        var target = NewEntryPath(project, parent, name);
        if (File.Exists(target) || Directory.Exists(target)) throw new StudioXException("FILE_EXISTS", "同名文件或文件夹已存在。");
        token.ThrowIfCancellationRequested();
        if (directory) Directory.CreateDirectory(target);
        else { await using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true); }
        return Path.GetRelativePath(project, target).Replace('\\', '/');
    }

    public string RenameEntry(string project, string relative, string name)
    {
        if (!CanRenameEntry(relative)) throw new StudioXException("FILE_MANAGED", "工程根目录及固定配置不能在工程树中重命名。");
        var source = GetEntryPath(project, relative);
        var target = NewEntryPath(project, ParentDirectory(relative), name);
        if (source.Equals(target, StringComparison.Ordinal)) return relative;
        var caseOnly = source.Equals(target, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(target) || Directory.Exists(target))) throw new StudioXException("FILE_EXISTS", "同名文件或文件夹已存在。");
        var directory = Directory.Exists(source);
        if (!directory && (File.GetAttributes(source) & FileAttributes.ReadOnly) != 0) throw new StudioXException("EDITOR_READ_ONLY", "此文件为只读文件。");
        void Move(string from, string to) { if (directory) Directory.Move(from, to); else File.Move(from, to); }
        if (caseOnly)
        {
            // Windows 大小写重命名经过临时名称，失败时恢复；不删除源文件。
            var temporary = PathBoundary.Resolve(project, (ParentDirectory(relative) is { Length: > 0 } parent ? parent + "/" : "") + ".studiox-rename-" + Guid.NewGuid().ToString("N"));
            Move(source, temporary);
            try { Move(temporary, target); }
            catch { Move(temporary, source); throw; }
        }
        else Move(source, target);
        return Path.GetRelativePath(project, target).Replace('\\', '/');
    }

    public Task<IReadOnlyList<string>> CopyEntriesAsync(string project, string parent, IReadOnlyList<string> sources, CancellationToken token = default) => Task.Run(async () =>
    {
        if (sources.Count is < 1 or > 256) throw new StudioXException("FILE_SELECTION", "一次请选择 1–256 个文件或文件夹。");
        var plans = new List<(string Source, string Target, string Relative, bool Directory)>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in sources.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
            EnsureCopySource(source);
            var directory = Directory.Exists(source);
            var name = Path.GetFileName(source);
            var target = NewEntryPath(project, parent, name);
            var stem = directory ? name : Path.GetFileNameWithoutExtension(name);
            var extension = directory ? "" : Path.GetExtension(name);
            for (var number = 1; File.Exists(target) || Directory.Exists(target) || reserved.Contains(target); number++)
                target = NewEntryPath(project, parent, stem + (number == 1 ? " - 副本" : $" - 副本 ({number})") + extension);
            if (directory && target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new StudioXException("FILE_COPY_SELF", "不能把文件夹复制到它自身或它的子目录中。");
            reserved.Add(target);
            plans.Add((source, target, Path.GetRelativePath(project, target).Replace('\\', '/'), directory));
        }
        var stagingRelative = (parent.Length > 0 ? parent + "/" : "") + ".studiox-copy-" + Guid.NewGuid().ToString("N");
        var staging = PathBoundary.Resolve(project, stagingRelative);
        Directory.CreateDirectory(staging);
        var committed = new List<(string Relative, bool Directory)>();
        try
        {
            for (var i = 0; i < plans.Count; i++) await CopyAsync(plans[i].Source, Path.Combine(staging, i.ToString(System.Globalization.CultureInfo.InvariantCulture)), token).ConfigureAwait(false);
            for (var i = 0; i < plans.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var item = plans[i]; var staged = Path.Combine(staging, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var target = PathBoundary.Resolve(project, item.Relative);
                if (item.Directory) Directory.Move(staged, target); else File.Move(staged, target);
                committed.Add((item.Relative, item.Directory));
            }
            return (IReadOnlyList<string>)plans.Select(item => item.Relative).ToArray();
        }
        catch
        {
            // 回滚只涉及本次新建的副本，目标路径重新验证，已有同名文件从不覆盖。
            foreach (var item in committed)
            {
                var target = PathBoundary.Resolve(project, item.Relative);
                if (item.Directory) Directory.Delete(target, recursive: true); else File.Delete(target);
            }
            throw;
        }
        finally { Directory.Delete(PathBoundary.Resolve(project, stagingRelative), recursive: true); }
    }, token);

    private static void EnsureCopySource(string full)
    {
        if (!File.Exists(full) && !Directory.Exists(full)) throw new StudioXException("FILE_MISSING", "复制源已不存在：" + full);
        // 外部剪贴板允许选择文件；拒绝沿链接访问别的目录，递归时逐项检查。
        for (string? path = full; path is not null; path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new StudioXException("PATH_LINK", "复制暂不支持链接文件或目录：" + full);
    }
    private static async Task CopyAsync(string source, string target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); EnsureCopySource(source);
        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(target);
            foreach (var child in Directory.EnumerateFileSystemEntries(source)) await CopyAsync(child, Path.Combine(target, Path.GetFileName(child)), token).ConfigureAwait(false);
        }
        else
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await input.CopyToAsync(output, token).ConfigureAwait(false);
        }
    }

    public void ShowInExplorer(string project, string relative)
    {
        var path = GetEntryPath(project, relative);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
        start.Arguments = Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"";
        using var process = Process.Start(start);
    }
}
