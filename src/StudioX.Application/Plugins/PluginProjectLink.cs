namespace StudioX.Application.Plugins;

using System.Text.Json;
using System.Text.Json.Serialization;
using StudioX.Foundation;

/// <summary>用户点击后打开工程的版本化面板数据；不会触发插件宿主工具。</summary>
public sealed record PluginProjectLink(int FormatVersion, string Directory, string? BuildLog = null, string? BuildRecord = null)
{
    public static PluginProjectLink Parse(JsonElement value)
    {
        var options = new JsonSerializerOptions(JsonStore.Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        var link = value.Deserialize<PluginProjectLink>(options);
        if (link is null || link.FormatVersion != 1 || string.IsNullOrWhiteSpace(link.Directory) ||
            link.Directory.Length > 4096 || link.Directory.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(link.Directory) || link.Directory.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new StudioXException("PLUGIN_PROJECT_LINK", "工程入口必须提供格式 1 和明确的本地工程绝对路径。");
        }
        if ((link.BuildLog is null) != (link.BuildRecord is null))
        {
            throw new StudioXException("PLUGIN_PROJECT_LINK", "编译日志必须同时指定对应的编译记录。");
        }
        foreach (var relative in new[] { link.BuildLog, link.BuildRecord }.OfType<string>())
        {
            if (relative.Length > 4096)
            {
                throw new StudioXException("PLUGIN_PROJECT_LINK", "编译记录路径过长。");
            }
            _ = PathBoundary.Resolve(link.Directory, relative);
        }
        return link;
    }
}
