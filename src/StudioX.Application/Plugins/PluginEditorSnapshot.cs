namespace StudioX.Application.Plugins;

/// <summary>当前缓冲区状态；DiskHash 是保存时的磁盘哈希，不是未保存内容的哈希。</summary>
public sealed record PluginEditorSnapshot(string RelativePath, string Text, bool IsDirty, string DiskHash);
