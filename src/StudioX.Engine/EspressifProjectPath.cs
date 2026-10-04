namespace StudioX.Engine;

using StudioX.Foundation;

/// <summary>新目录尚无短名称，预览时只接受可确定使用的路径，不为检查创建临时工程。</summary>
public static class EspressifProjectPath
{
    public static void ValidateNewDirectory(string destination)
    {
        var full = Path.GetFullPath(destination);
        var ancestor = Path.GetDirectoryName(full)!;
        while (!Directory.Exists(ancestor))
        {
            ancestor = Path.GetDirectoryName(ancestor)
            ?? throw new StudioXException("ESPRESSIF_PATH", "验证副本缺少有效的父目录。");
        }
        var parent = EspressifNativePath.For(ancestor);
        var proposed = Path.Combine(parent, Path.GetRelativePath(ancestor, full));
        if (proposed.Any(c => c > 127 || char.IsWhiteSpace(c)) || proposed.Contains(';'))
        {
            throw new StudioXException("ESPRESSIF_PATH", "新建 ESP 验证副本请使用不含空格、分号的 ASCII 目录名；尚未创建的目录不能保证有 Windows 短路径。请选择可用父目录并调整副本名称。");
        }
    }
}
