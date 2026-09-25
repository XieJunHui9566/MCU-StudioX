namespace StudioX.Application;

using StudioX.Foundation;

public sealed record RecentProject(string Name, string Directory, DateTimeOffset LastOpened);
public sealed class RecentProjectService(string dataDirectory)
{
    private readonly string path = Path.Combine(dataDirectory, "recent-projects.json");
    private readonly SemaphoreSlim mutationGate = new(1, 1);

    // 欢迎页先读取记录，不在启动关键路径上探测历史工程所在磁盘。
    public async Task<IReadOnlyList<RecentProject>> LoadAsync(CancellationToken token = default) => File.Exists(path)
        ? await JsonStore.ReadAsync<List<RecentProject>>(path, token) : [];

    public async Task RememberAsync(string name, string directory, CancellationToken token = default)
    {
        var full = Path.GetFullPath(directory);
        await mutationGate.WaitAsync(token);
        try
        {
            var existing = await LoadAsync(token);
            var updated = new[] { new RecentProject(name, full, DateTimeOffset.Now) }
                .Concat(existing.Where(project => !SameDirectory(project.Directory, full))).Take(12).ToArray();
            await JsonStore.WriteAsync(path, updated, token);
        }
        finally { mutationGate.Release(); }
    }

    public async Task<bool> RemoveAsync(string directory, CancellationToken token = default)
    {
        var full = Path.GetFullPath(directory);
        await mutationGate.WaitAsync(token);
        try
        {
            var existing = await LoadAsync(token);
            var updated = existing.Where(project => !SameDirectory(project.Directory, full)).ToArray();
            if (updated.Length == existing.Count) return false;
            await JsonStore.WriteAsync(path, updated, token);
            return true;
        }
        finally { mutationGate.Release(); }
    }

    // true 表示路径确认为本地固定磁盘上的失效工程；记录可能已被并发清理。
    public async Task<bool> RemoveIfMissingLocalAsync(string directory, CancellationToken token = default)
    {
        var full = Path.GetFullPath(directory);
        await mutationGate.WaitAsync(token);
        try
        {
            if (!await Task.Run(() => IsMissingLocalProject(full), token)) return false;
            var existing = await LoadAsync(token);
            var updated = existing.Where(project => !SameDirectory(project.Directory, full)).ToArray();
            if (updated.Length != existing.Count) await JsonStore.WriteAsync(path, updated, token);
            return true;
        }
        finally { mutationGate.Release(); }
    }

    public async Task<IReadOnlyList<RecentProject>> PruneMissingLocalAsync(CancellationToken token = default)
    {
        await mutationGate.WaitAsync(token);
        try
        {
            var existing = await LoadAsync(token);
            if (existing.Count == 0) return existing;
            var missing = await Task.Run(() =>
            {
                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var project in existing)
                {
                    token.ThrowIfCancellationRequested();
                    if (IsMissingLocalProject(project.Directory)) result.Add(project.Directory);
                }
                return result;
            }, token);
            if (missing.Count == 0) return existing;
            var updated = existing.Where(project => !missing.Contains(project.Directory)).ToArray();
            await JsonStore.WriteAsync(path, updated, token);
            return updated;
        }
        finally { mutationGate.Release(); }
    }

    private static bool SameDirectory(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsMissingLocalProject(string directory)
    {
        try
        {
            if (!Path.IsPathFullyQualified(directory)) return false;
            var root = Path.GetPathRoot(directory);
            // UNC、映射网络盘和可移动盘离线时不能推断用户已经删除工程。
            if (root is not { Length: 3 } || root[1] != ':' || (root[2] != '\\' && root[2] != '/')) return false;
            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) return false;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }

        try
        {
            var attributes = File.GetAttributes(Path.Combine(directory, ".studiox", "project.json"));
            return (attributes & FileAttributes.Directory) != 0;
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }
}
