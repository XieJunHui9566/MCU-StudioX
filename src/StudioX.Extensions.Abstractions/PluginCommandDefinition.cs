namespace StudioX.Extensions.Abstractions;

/// <summary>供命令面板、菜单或工具栏呈现的命令数据。</summary>
public sealed record PluginCommandDefinition(string Id, string Title, string Placement = "palette", string? Shortcut = null);
