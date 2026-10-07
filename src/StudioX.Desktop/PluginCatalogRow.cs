namespace StudioX.Desktop;

using StudioX.Application.Plugins;

/// <summary>把已校验目录信息转换为可显示状态，不持有插件程序集。</summary>
internal sealed class PluginCatalogRow(PluginCatalogEntry entry, bool busy, bool settings = false, bool debug = false)
{
    public PluginCatalogEntry Entry { get; } = entry;
    public string DisplayName => Entry.DisplayName;
    public string IdentityText => $"{Entry.Id}  ·  {Entry.Version}  ·  API {Entry.Manifest?.ApiVersion.ToString() ?? "未知"}";
    public string Description => Entry.Manifest is null ? "清单读取失败；请查看诊断。"
        : string.IsNullOrWhiteSpace(Entry.Manifest.Description) ? "此插件未提供说明。" : Entry.Manifest.Description;
    public string CapabilityText => "声明能力：" + string.Join("、", Entry.Capabilities.Select(CapabilityName));
    public string StateText => Entry.Diagnostic ?? (Entry.Enabled
        ? Entry.Manifest?.Scope == "application" ? "已启用 · 无需工程" : "已启用 · 有工程时运行"
        : "已安装 · 未运行");
    public string ToggleText => Entry.Enabled ? "停用" : "启用并允许运行";
    public bool CanToggle => !busy && (Entry.Enabled || Entry.CanEnable);
    public bool CanUninstall => !busy && !Entry.IsBundled;
    public bool HasSettings => Entry.Capabilities.Contains("settings");
    public bool HasDebug => Entry.Capabilities.Contains("debugAdapters");
    public bool CanSettings => !busy && settings;
    public bool CanDebug => !busy && debug;
    public string OriginText => Entry.IsBundled ? "来源：IDE 内置" : "来源：用户安装";
    internal static string CapabilityName(string capability) => capability switch
    {
        "commands" => "命令",
        "panels" => "面板",
        "agentTools" => "Agent 工具",
        "settings" => "设置",
        "events" => "工程/文档事件",
        "languages" => "语言补全",
        "debugAdapters" => "调试快照",
        "decode" => "数据解码",
        _ => capability
    };
}
