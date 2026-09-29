namespace StudioX.Extensions;

using StudioX.Foundation;

/// <summary>插件声明的左侧入口；只携带有界数据，不依赖桌面控件或执行插件界面代码。</summary>
public sealed record PluginActivityDefinition(int Version = 1, string? Title = null, string? Tooltip = null, PluginActivityIcon? Icon = null)
{
    public void Validate()
    {
        if (Version != 1 || Title is not null && (string.IsNullOrWhiteSpace(Title) || Title.Length > 80 || Title.Any(char.IsControl)) ||
            Tooltip?.Length > 1024)
        {
            throw new StudioXException("PLUGIN_ACTIVITY", "插件入口版本或名称无效。");
        }
        Icon?.Validate();
    }
}
