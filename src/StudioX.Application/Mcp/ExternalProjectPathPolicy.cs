namespace StudioX.Application.Mcp;

using StudioX.Foundation;

/// <summary>外部目录的只读路径边界与敏感文件过滤；PDF、LVGL、浏览和复制使用同一规则。</summary>
internal static class ExternalProjectPathPolicy
{
    internal static string CombineExternalPath(string sourcePath, string relative) =>
        sourcePath.Length == 0 ? relative : relative.Length == 0 ? sourcePath : sourcePath + "/" + relative;

    private static readonly HashSet<string> ExternalExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".studiox", ".build", ".vscode", ".settings", ".cache", "build", "bin", "obj",
        "debug", "release", "node_modules", "artifacts", "secrets", "credentials", "private", "keys", "certs"
    };
    private static readonly HashSet<string> ExternalTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".hh", ".s", ".asm", ".inc",
        ".ld", ".icf", ".cmake", ".ioc", ".json", ".txt", ".md", ".py", ".xml",
        ".cfg", ".conf", ".ini", ".csv", ".yml", ".yaml", ".v", ".vh", ".sv", ".svh"
    };
    private static readonly HashSet<string> ExternalBlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".msi", ".bat", ".cmd", ".ps1", ".pfx", ".p12", ".pem",
        ".key", ".crt", ".cer", ".db", ".sqlite", ".sqlite3"
    };

    internal static string ValidateExternalRoot(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory.Length > 2_048 || !Path.IsPathFullyQualified(directory) ||
            directory.StartsWith("\\\\?\\", StringComparison.Ordinal) || directory.StartsWith("\\\\.\\", StringComparison.Ordinal))
        {
            throw new StudioXException("MCP_EXTERNAL_ROOT", "外部目录必须是普通绝对路径。");
        }
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (root.Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root) ||
            IsExcludedExternalName(Path.GetFileName(root)))
        {
            throw new StudioXException("MCP_EXTERNAL_ROOT", "请选择存在的工程或通用库目录，不能选择磁盘根目录或受保护目录。");
        }
        EnsureNoReparseAncestors(root);
        return root;
    }

    internal static void EnsureNoReparseAncestors(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(current));
            if (name.Length > 0 && IsExcludedExternalName(name))
            {
                throw new StudioXException("MCP_EXTERNAL_ROOT", "外部目录位于隐藏、构建或凭据目录内，不能授权读取。");
            }
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("MCP_EXTERNAL_LINK", "外部目录或其父目录含符号链接/重解析点，不能授权读取。");
            }
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
    }

    internal static string ResolveExternalPath(string root, string relative, bool allowRoot = false)
    {
        if (allowRoot && relative.Length == 0)
        {
            return root;
        }
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 1_024 ||
            relative.Split('/').Any(IsExcludedExternalName))
        {
            throw new StudioXException("MCP_EXTERNAL_PATH", "外部路径为空、过长或包含受保护目录/文件。");
        }
        return PathBoundary.Resolve(root, relative);
    }

    internal static bool IsExcludedExternalName(string name) =>
        name.Length == 0 || name.StartsWith('.') || ExternalExcludedDirectories.Contains(name) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("id_rsa", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("id_ed25519", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("token.", StringComparison.OrdinalIgnoreCase);

    internal static bool IsExternalCopyFile(string name) =>
        !IsExcludedExternalName(name) && !ExternalBlockedExtensions.Contains(Path.GetExtension(name));

    internal static bool IsExternalTextFile(string name) =>
        IsExternalCopyFile(name) &&
        (ExternalTextExtensions.Contains(Path.GetExtension(name)) ||
         name.Equals("Makefile", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("NOTICE", StringComparison.OrdinalIgnoreCase));

}
