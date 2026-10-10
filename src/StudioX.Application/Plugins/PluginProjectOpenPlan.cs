namespace StudioX.Application.Plugins;

/// <summary>经过工程身份与编译日志核对的打开计划；源码哈希用于撤销过期的错误标记。</summary>
public sealed record PluginProjectOpenPlan(string Directory, string? BuildOutput,
    IReadOnlyDictionary<string, string> SourceHashes, string? Diagnostic);
