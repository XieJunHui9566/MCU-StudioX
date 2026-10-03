namespace StudioX.Engine;

using StudioX.Foundation;

/// <summary>格式 1 默认 7z、兼容已有 ZIP；保留原 toolset.json 字节和工程内容锁。</summary>
public static class ToolchainArchiveFormat
{
    public const string Extension = ".mcutoolchain";
    public const string LegacyExtension = ".studioxtools";

    public static void ValidateFileName(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(Extension, StringComparison.OrdinalIgnoreCase) && !extension.Equals(LegacyExtension, StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("TOOLS_ARCHIVE_FORMAT", "请选择 .mcutoolchain 开发环境组件包；也支持已有 .studioxtools 离线归档。");
    }

    public static string CacheExtension(string source)
    {
        var path = Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == "https" ? uri.AbsolutePath : source;
        // 旧目录保留原缓存名，已落盘的下载片段和锁文件无需迁移。
        return Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase) ? Extension : LegacyExtension;
    }
}
