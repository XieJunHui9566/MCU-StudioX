namespace StudioX.Engine;

/// <summary>开发环境组件归档内的普通文件；路径和大小已通过容器层检查。</summary>
public sealed record ToolchainArchiveEntry(string Name, long Length);
