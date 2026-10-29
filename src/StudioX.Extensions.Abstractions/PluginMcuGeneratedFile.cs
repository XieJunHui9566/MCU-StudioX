namespace StudioX.Extensions.Abstractions;

/// <summary>生成目录内的 UTF-8 源码；不接受脚本、构建指令或用户文件修改内容。</summary>
public sealed record PluginMcuGeneratedFile(string Path, string Content);
