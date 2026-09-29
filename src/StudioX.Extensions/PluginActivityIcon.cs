namespace StudioX.Extensions;

using StudioX.Foundation;

/// <summary>24×24 坐标系中的单色折线；主题颜色由宿主统一提供。</summary>
public sealed record PluginActivityIcon(double[][] Strokes)
{
    public void Validate()
    {
        if (Strokes is null || Strokes.Length is < 1 or > 32)
        {
            throw new StudioXException("PLUGIN_ACTIVITY_ICON", "插件图标须包含 1–32 条折线。");
        }
        var coordinates = 0;
        foreach (var stroke in Strokes)
        {
            if (stroke is null || stroke.Length is < 4 or > 256 || stroke.Length % 2 != 0 ||
                stroke.Any(value => !double.IsFinite(value) || value is < 0 or > 24))
            {
                throw new StudioXException("PLUGIN_ACTIVITY_ICON", "图标折线须为 2–128 个坐标点，坐标范围为 0–24。");
            }
            coordinates += stroke.Length;
        }
        if (coordinates > 2048)
        {
            throw new StudioXException("PLUGIN_ACTIVITY_ICON", "插件图标超过 1024 个坐标点。");
        }
    }
}
