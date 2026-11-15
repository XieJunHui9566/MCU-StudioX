namespace StudioX.Engine;

using StudioX.Foundation;

/// <summary>预览只校验目标路径；旧工具的字符集限制由私有构建目录处理，不创建占位工程。</summary>
public static class EspressifProjectPath
{
    public static void ValidateNewDirectory(string destination)
    {
        var full = Path.GetFullPath(destination);
        var ancestor = Path.GetDirectoryName(full)
            ?? throw new StudioXException("ESPRESSIF_PATH", "工程副本需要独立目录，不能直接使用磁盘根目录。");
        while (!Directory.Exists(ancestor))
        {
            ancestor = Path.GetDirectoryName(ancestor)
            ?? throw new StudioXException("ESPRESSIF_PATH", "验证副本缺少有效的父目录。");
        }
        _ = PathBoundary.Resolve(ancestor, Path.GetRelativePath(ancestor, full).Replace('\\', '/'));
    }
}
