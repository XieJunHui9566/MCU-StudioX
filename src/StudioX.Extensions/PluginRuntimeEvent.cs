namespace StudioX.Extensions;

using System.Text.Json;

/// <summary>插件宿主发布的面板更新或结构化日志。</summary>
public sealed record PluginRuntimeEvent(string Kind, JsonElement Payload);
