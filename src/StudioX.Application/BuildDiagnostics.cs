namespace StudioX.Application;

using System.Text.RegularExpressions;

public sealed record BuildDiagnostic(string RelativePath, int Line, int Column, bool IsWarning, string Message);

/// <summary>从实际构建输出提取源文件位置；不推测没有行号的链接错误。</summary>
public static partial class BuildDiagnostics
{
    public static IReadOnlyList<BuildDiagnostic> Parse(string project, string output)
    {
        var result = new List<BuildDiagnostic>();
        var lines = Ansi().Replace(output, "").Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var match = Compiler().Match(lines[index]);
            var cmake = false;
            if (!match.Success) { match = CMake().Match(lines[index]); cmake = true; }
            if (!match.Success || !int.TryParse(match.Groups["line"].Value, out var line) || line < 1) continue;
            var path = ResolvePath(project, match.Groups["file"].Value, cmake);
            if (path is null) continue;
            var column = int.TryParse(match.Groups["column"].Value, out var parsed) ? Math.Max(0, parsed) : 0;
            var message = match.Groups["message"].Value.Trim();
            if (cmake)
            {
                // CMake 的正文在后续缩进行；保留具体配置错误，而非仅显示命令名。
                for (var next = index + 1; next < lines.Length; next++)
                {
                    if (lines[next].Length > 0 && !char.IsWhiteSpace(lines[next][0])) break;
                    if (lines[next].Length == 0 && message.Length > 0) break;
                    if (!string.IsNullOrWhiteSpace(lines[next])) message += (message.Length == 0 ? "" : "\n") + lines[next].Trim();
                }
            }
            if (message.Length == 0) message = lines[index].Trim();
            result.Add(new(path, line, column, match.Groups["severity"].Value.StartsWith("warning", StringComparison.OrdinalIgnoreCase), message));
        }
        return result.Distinct().ToArray();
    }

    private static string? ResolvePath(string project, string file, bool cmake)
    {
        try
        {
            var root = Path.GetFullPath(project);
            file = file.Trim().Trim('"', '\'').Replace('/', Path.DirectorySeparatorChar);
            if (file.StartsWith('<')) return null;
            // Ninja 的相对路径以 .build 为基准；CMake 的位置通常相对源码根目录。
            var candidates = Path.IsPathRooted(file) ? [file] : cmake
                ? new[] { Path.Combine(root, file), Path.Combine(root, ".build", file) }
                : [Path.Combine(root, ".build", file), Path.Combine(root, file)];
            foreach (var candidate in candidates)
            {
                var absolute = Path.GetFullPath(candidate);
                var relative = Path.GetRelativePath(root, absolute).Replace('\\', '/');
                if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)) continue;
                if (File.Exists(absolute)) return relative;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        return null;
    }

    [GeneratedRegex(@"^\s*(?<file>.+?):(?<line>\d+)(?::(?<column>\d+))?:\s*(?<severity>fatal error|error|warning):\s*(?<message>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex Compiler();
    [GeneratedRegex(@"^CMake (?<severity>Error|Warning)(?: \([^)]*\))? at (?<file>.+?):(?<line>\d+)(?: \([^)]*\))?:\s*(?<message>.*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CMake();
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex Ansi();
}
