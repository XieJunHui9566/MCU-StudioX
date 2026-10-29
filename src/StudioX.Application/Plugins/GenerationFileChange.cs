namespace StudioX.Application.Plugins;

/// <summary>事务持久化前后字节，用于中断恢复；不把开发机路径写入工程。</summary>
internal sealed record GenerationFileChange(string Path, byte[]? Before, byte[]? After);
