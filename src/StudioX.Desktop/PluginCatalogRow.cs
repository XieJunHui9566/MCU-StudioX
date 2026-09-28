namespace StudioX.Desktop;

using StudioX.Application.Plugins;

/// <summary>把已校验目录信息转换为可显示状态，不持有插件程序集。</summary>
internal sealed class PluginCatalogRow(PluginCatalogEntry entry, bool busy)
{
    public PluginCatalogEntry Entry { get; } = entry;
    public string DisplayName => Entry.DisplayName;
    public string IdentityText => $"{Entry.Id}  ·  {Entry.Version}  ·  API {Entry.Manifest?.ApiVersion.ToString() ?? "未知"}";
    public string CapabilityText => "声明能力：" + string.Join("、", Entry.Capabilities);
    public string StateText => Entry.Diagnostic ?? (Entry.Enabled ? "已启用 · 有工程时运行" : "已安装 · 未运行");
    public string ToggleText => Entry.Enabled ? "停用" : "启用并允许运行";
    public bool CanToggle => !busy && (Entry.Enabled || Entry.CanEnable);
    public bool CanUninstall => !busy && !Entry.IsBundled;
}
