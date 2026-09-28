namespace StudioX.Extensions.Abstractions;

/// <summary>插件声明的命令、面板与 Agent 工具；声明本身不授予主机工具权限。</summary>
public sealed record PluginContribution(
    PluginCommandDefinition[] Commands,
    PluginPanelDefinition[] Panels,
    PluginAgentToolDefinition[] AgentTools);
