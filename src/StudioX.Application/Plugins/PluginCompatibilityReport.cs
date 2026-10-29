namespace StudioX.Application.Plugins;

public sealed record PluginCompatibilityReport(IReadOnlyList<string> Missing)
{
    public bool Compatible => Missing.Count == 0;
    public string Summary => Compatible ? "当前宿主满足插件声明的契约能力。" : "当前宿主缺少：" + string.Join("；", Missing);
}
