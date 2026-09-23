namespace StudioX.Application;

using StudioX.Foundation;

public sealed record RecentProject(string Name, string Directory, DateTimeOffset LastOpened);
public sealed class RecentProjectService(string dataDirectory)
{
    private readonly string path = Path.Combine(dataDirectory, "recent-projects.json");
    public async Task<IReadOnlyList<RecentProject>> LoadAsync(CancellationToken token = default) => File.Exists(path)
        ? await JsonStore.ReadAsync<List<RecentProject>>(path, token) : [];
    public async Task RememberAsync(string name, string directory, CancellationToken token = default)
    {
        var full = Path.GetFullPath(directory);
        var existing = await LoadAsync(token);
        var updated = new[] { new RecentProject(name, full, DateTimeOffset.Now) }.Concat(existing.Where(p => !p.Directory.Equals(full, StringComparison.OrdinalIgnoreCase))).Take(12).ToArray();
        await JsonStore.WriteAsync(path, updated, token);
    }
}
