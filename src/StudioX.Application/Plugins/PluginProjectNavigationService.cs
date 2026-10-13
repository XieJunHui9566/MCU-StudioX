namespace StudioX.Application.Plugins;

using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;

/// <summary>准备用户点击的工程入口；目标验证失败不会影响当前工作区，日志失败仍允许打开工程。</summary>
public sealed class PluginProjectNavigationService(ProjectFileService files)
{
    public async Task<PluginProjectOpenPlan> PrepareAsync(JsonElement value, CancellationToken token = default)
    {
        var link = PluginProjectLink.Parse(value);
        var directory = Path.GetFullPath(link.Directory);
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("PLUGIN_PROJECT_LINK", "工程目录不存在或经过链接，未切换工程。");
            }
        }
        _ = PathBoundary.Resolve(directory, ".studiox/project.json");
        _ = await ProjectService.ReadAsync(directory, token).ConfigureAwait(false);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? output = null;
        string? diagnostic = null;
        if (link.BuildLog is not null)
        {
            try
            {
                var log = await files.ReadAsync(directory, link.BuildLog, token).ConfigureAwait(false);
                output = log.Text;
                var record = await files.ReadAsync(directory, link.BuildRecord!, token).ConfigureAwait(false);
                using var json = JsonDocument.Parse(record.Text);
                var root = json.RootElement;
                if (root.GetProperty("formatVersion").GetInt32() != 1 || root.GetProperty("log").GetString() != link.BuildLog ||
                    !log.DiskHash.Equals(root.GetProperty("logSha256").GetString(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new StudioXException("PLUGIN_PROJECT_LOG", "编译日志与记录不一致，只显示原文，不发布错误标记。");
                }
                foreach (var item in root.GetProperty("sourceHashes").EnumerateObject())
                {
                    _ = PathBoundary.Resolve(directory, item.Name);
                    var hash = item.Value.GetString();
                    if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit) || !hashes.TryAdd(item.Name, hash))
                    {
                        throw new StudioXException("PLUGIN_PROJECT_LOG", "编译记录的源码哈希无效。");
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or
                ArgumentException or JsonException or KeyNotFoundException or StudioXException)
            {
                hashes.Clear();
                diagnostic = error.ToString();
            }
        }
        return new(directory, output, hashes, diagnostic);
    }
}
