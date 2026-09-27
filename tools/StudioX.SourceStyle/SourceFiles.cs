namespace StudioX.SourceStyle;

/// <summary>限定第一方源码；显式排除生成输出、工具运行时和厂商代码。</summary>
internal static class SourceFiles
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".build", "runtime", "artifacts", "vendor", "third-party", "node_modules"
    };

    public static IEnumerable<string> Enumerate(string root)
    {
        foreach (var file in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "StudioX.slnx" })
        {
            var path = Path.Combine(root, file);
            if (File.Exists(path))
            {
                yield return path;
            }
        }
        foreach (var relative in new[] { "src", "tools", "examples" })
        {
            var directory = Path.Combine(root, relative);
            if (Directory.Exists(directory))
            {
                foreach (var file in Walk(directory))
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<string> Walk(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (!Path.GetFileName(file).Contains("_wpftmp", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (ExcludedDirectories.Contains(Path.GetFileName(child)) ||
                (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }
            foreach (var file in Walk(child))
            {
                yield return file;
            }
        }
    }
}
