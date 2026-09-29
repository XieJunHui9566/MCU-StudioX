namespace StudioX.Extensions.Abstractions;

using System.Text.Json;

/// <summary>API 3 的声明式设置；类型为 string、boolean 或 integer。</summary>
public sealed record PluginSettingDefinition(string Id, string Title, string Type, JsonElement Default,
    string Description = "", int? Minimum = null, int? Maximum = null);
