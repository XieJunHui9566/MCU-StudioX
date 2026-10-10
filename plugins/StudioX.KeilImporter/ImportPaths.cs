namespace StudioX.KeilImporter;

using System.Security.Cryptography;
using System.Text;

/// <summary>新目录事务和只读输入共用边界；拒绝链接，避免选择路径被重定向。</summary>
public static class ImportPaths
{
    public static string Absolute(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.Any(character => character < 32))
        {
            throw new InvalidOperationException("请填写本机绝对路径；本版不支持网络目录。");
        }
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        RejectLinks(full);
        return full;
    }

    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("不支持链接、联接点或重解析路径：" + current);
            }
        }
    }

    public static bool Within(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static string ResolveSource(string root, string projectDirectory, string value)
    {
        var normalized = value.Trim().Trim('"').Replace('\\', Path.DirectorySeparatorChar);
        if (normalized.Length == 0 || normalized.Contains('$') || normalized.Contains('%'))
        {
            throw new InvalidOperationException("无法解析 Keil 路径变量，请将依赖放入源码根目录并使用相对路径：" + value);
        }
        var full = Absolute(Path.GetFullPath(normalized, projectDirectory));
        if (!Within(root, full))
        {
            throw new InvalidOperationException("依赖在指定源码根目录之外，请扩大源码根目录或先整理依赖副本：" + value);
        }
        return full;
    }

    public static string Relative(string root, string path)
    {
        if (!Within(root, path))
        {
            throw new InvalidOperationException("文件越过源码边界。");
        }
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (relative == ".")
        {
            return relative;
        }
        _ = CMakeValue(relative);
        return relative;
    }

    public static string PackPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || Path.IsPathRooted(relative) ||
            relative.Split('/').Any(segment => segment is "" or "." or ".." || segment.Contains(':')))
        {
            throw new InvalidOperationException("器件包包含无效相对路径：" + relative);
        }
        return ResolveSource(root, root, relative);
    }

    public static string ProjectName(string name)
    {
        if (name.Length is < 1 or > 80 || !char.IsAsciiLetterOrDigit(name[0]) ||
            name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.') ||
            name.EndsWith('.') || name.Contains("..", StringComparison.Ordinal) ||
            new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
                .Contains(name.Split('.')[0], StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("工程名使用 1–80 位英文字母、数字、下划线、短横线或点，不能使用 Windows 保留名称。");
        }
        return name;
    }

    public static string CMakeValue(string value)
    {
        // 不接受 CMake 列表分隔和变量展开；工程内容不能注入生成配置。
        if (value.Length == 0 || value.Any(character => character < 32 || character is ';' or '$' or '"' or '\\'))
        {
            throw new InvalidOperationException("CMake 参数含本版无法可靠转义的字符：" + value);
        }
        return '"' + value + '"';
    }

    public static async Task<string> HashAsync(string path, CancellationToken token)
    {
        RejectLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }

    public static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
