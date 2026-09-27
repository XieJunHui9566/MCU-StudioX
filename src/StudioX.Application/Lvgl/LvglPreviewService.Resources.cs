namespace StudioX.Application.Lvgl;

using StudioX.Engine.Lvgl;
using StudioX.Foundation;

public sealed partial class LvglPreviewService
{
    private static string PrepareResourceSlot(LvglPreviewBuildResult result, string slot)
    {
        if (result.RuntimeResourcesDirectory is null)
        {
            return result.BuildDirectory;
        }
        var staging = PathBoundary.Resolve(result.BuildDirectory, "resources-next");
        if (!Path.GetFullPath(result.RuntimeResourcesDirectory).Equals(staging, StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("LVGL_RESOURCE_STAGE", "预览资源 staging 路径无效。");
        }
        var destination = PathBoundary.Resolve(result.BuildDirectory, "resources-active-" + slot);
        DeleteResourceSlot(result.BuildDirectory, destination);
        Directory.Move(staging, destination);
        return destination;
    }

    private void TryCleanResourceSlot(Session session, string directory)
    {
        var build = PathBoundary.Resolve(session.Project, ".build/pc-preview");
        if (directory.Equals(build, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        try
        {
            DeleteResourceSlot(build, directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StudioXException)
        {
            AppendLog(session, "[预览资源回收失败] " + ex + "\n");
        }
    }

    private static void DeleteResourceSlot(string build, string directory)
    {
        var resolved = Path.GetFullPath(directory);
        if (!new[] { "resources-active-a", "resources-active-b" }.Any(name =>
            resolved.Equals(PathBoundary.Resolve(build, name), StringComparison.OrdinalIgnoreCase)))
        {
            throw new StudioXException("LVGL_RESOURCE_CLEANUP", "资源回收仅允许当前预览生成的固定 slot 目录。");
        }
        if (!Directory.Exists(resolved))
        {
            return;
        }
        var pending = new Stack<string>();
        pending.Push(resolved);
        while (pending.TryPop(out var current))
        {
            foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new StudioXException("LVGL_RESOURCE_LINK", "生成资源目录出现重解析点，停止回收：" + entry.Name);
                }
                if (entry is DirectoryInfo)
                {
                    pending.Push(entry.FullName);
                }
            }
        }
        Directory.Delete(resolved, true);
    }
}
