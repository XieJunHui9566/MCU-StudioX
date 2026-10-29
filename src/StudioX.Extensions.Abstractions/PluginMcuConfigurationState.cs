namespace StudioX.Extensions.Abstractions;

/// <summary>配置预览的纯数据视图；配置求值和代码预览由隔离插件生成。</summary>
public sealed record PluginMcuConfigurationState(int FormatVersion, string Title, string Subtitle,
    string PackageName, double Width, double Height, PluginMcuPin[] Pins, PluginMcuClock[] Clocks,
    string SelectedPinId, PluginMcuField[] Fields, string[] Diagnostics, string GeneratedCode,
    string Status, bool Dirty, bool CanSave, bool CanGenerate);
