namespace StudioX.Application;

using StudioX.Foundation;

public sealed partial class RecentProjectService
{
    public Task<string> ValidateDeletionAsync(string directory, string? openProject, CancellationToken token = default) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(directory))
        {
            throw new StudioXException("PROJECT_DELETE_PATH", "工程位置必须是完整路径。");
        }
        var full = NormalizeDirectory(directory);
        if (openProject is not null && (ContainsDirectory(full, openProject) || ContainsDirectory(openProject, full)))
        {
            throw new StudioXException("PROJECT_DELETE_OPEN", "请先关闭此工程，再删除工程文件夹。未保存内容会在关闭时提示处理。");
        }
        var protectedPaths = new[] { dataDirectory, runtimeDirectory, AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) };
        if (SameDirectory(full, NormalizeDirectory(Path.GetPathRoot(full)!)) ||
            protectedPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Any(p => ContainsDirectory(full, p!)))
        {
            throw new StudioXException("PROJECT_DELETE_PROTECTED", "此位置包含 IDE、个人设置或系统目录，不能作为工程删除。");
        }
        if (!(await LoadAsync(token).ConfigureAwait(false)).Any(project => SameDirectory(NormalizeDirectory(project.Directory), full)))
        {
            throw new StudioXException("PROJECT_DELETE_RECENT", "此工程已不在最近列表，请刷新后再操作。");
        }
        var root = Path.GetPathRoot(full)!;
        var drive = new DriveInfo(root);
        if (full.StartsWith("\\\\", StringComparison.Ordinal) || drive.DriveType != DriveType.Fixed || !drive.IsReady)
        {
            throw new StudioXException("PROJECT_DELETE_DRIVE", "回收工程只支持就绪的本地固定磁盘。可移除最近记录，或在资源管理器中管理其它位置。");
        }
        // 不沿根目录或上级链接回收真实目标；工程内的链接由 Shell 按链接本身处理。
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("PATH_LINK", "不能从最近列表删除链接目录，请在资源管理器中核对实际位置。");
            }
        }
        var marker = PathBoundary.Resolve(full, ".studiox/project.json");
        if (!File.Exists(marker))
        {
            throw new StudioXException("PROJECT_DELETE_MARKER", "此位置已不是 StudioX 工程。可从最近列表移除记录，文件保持原样。");
        }
        return full;
    }, token);

    public async Task DeleteAsync(string directory, string? openProject, CancellationToken token = default)
    {
        // 确认对话框之后再次检查路径；Shell 失败或取消时不提前移除最近记录。
        var full = await ValidateDeletionAsync(directory, openProject, token).ConfigureAwait(false);
        await WindowsRecycleBin.RecycleDirectoryAsync(full, token).ConfigureAwait(false);
        await RemoveAsync(full, CancellationToken.None).ConfigureAwait(false);
    }

    private static string NormalizeDirectory(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    private static bool ContainsDirectory(string parent, string child)
    {
        parent = NormalizeDirectory(parent);
        child = NormalizeDirectory(child);
        return SameDirectory(parent, child) || child.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, HostPlatform.PathComparison);
    }
}
