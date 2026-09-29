namespace StudioX.Extensions.Abstractions;

/// <summary>API 3 语言扩展；使用 UTF-16 偏移，宿主丢弃过期文档响应。</summary>
public sealed record PluginLanguageDefinition(string Id, string DisplayName, string[] Extensions);
