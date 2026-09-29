namespace StudioX.Extensions.Abstractions;

/// <summary>插件声明的命令、面板与 Agent 工具；声明本身不授予主机工具权限。</summary>
public sealed record PluginContribution(
    PluginCommandDefinition[] Commands,
    PluginPanelDefinition[] Panels,
    PluginAgentToolDefinition[] AgentTools)
{
    public PluginSettingDefinition[] Settings { get; init; } = [];
    public string[] Events { get; init; } = [];
    public PluginLanguageDefinition[] Languages { get; init; } = [];
    public PluginDebugAdapterDefinition[] DebugAdapters { get; init; } = [];
}
