namespace StudioX.Application.Plugins;

using StudioX.Engine;

/// <summary>准确匹配且已启用的配置插件接管时钟；宿主崩溃不使旧手动频率恢复生效。</summary>
public sealed class PluginProjectClockOwnership(PluginManagerService plugins) : IProjectClockOwnership
{
    public async Task<bool> RequiresManagedClockAsync(string root, CancellationToken token = default)
    {
        foreach (var entry in await plugins.ListAsync(token))
        {
            if (entry.Enabled && entry.Manifest is { ManagesProjectClock: true } manifest &&
                await PluginProjectMatcher.MatchesAsync(manifest, root, token))
            {
                return true;
            }
        }
        return false;
    }
}
