namespace StudioX.Application.Plugins;

using System.Text.Json;

/// <summary>经过贡献与会话代次检查的插件面板、日志或退出事件。</summary>
public sealed record PluginWorkspaceEvent(string PluginId, string Kind, JsonElement Payload);
