namespace StudioX.Foundation;

/// <summary>所有包、工具和插件的路径均限制在所属目录内；不接受平台相关的别名路径。</summary>
public static class PathBoundary
{
    public static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || Path.IsPathRooted(relative))
            throw new StudioXException("PATH_UNSAFE", $"路径必须是正斜杠分隔的相对路径：{relative}");
        var parts = relative.Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(c => c < 32 || "<>:\"|?*".Contains(c)) || IsReserved(part))
                throw new StudioXException("PATH_UNSAFE", $"路径含非法组成部分：{relative}");
        }
        var absoluteRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(absoluteRoot, Path.Combine(parts)));
        if (!result.StartsWith(absoluteRoot, StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("PATH_ESCAPE", "路径超出了组件目录。");
        var current = absoluteRoot;
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new StudioXException("PATH_LINK", $"组件内不能包含符号链接或重解析点：{relative}");
        }
        return result;
    }

    private static bool IsReserved(string part)
    {
        var stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9');
    }
}
