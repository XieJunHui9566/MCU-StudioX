namespace StudioX.Extensions.Abstractions;

using System.Text.Json;

/// <summary>可注册到 Agent 的插件工具及其 JSON 输入模式。</summary>
public sealed record PluginAgentToolDefinition(string Id, string Description, JsonElement InputSchema);
