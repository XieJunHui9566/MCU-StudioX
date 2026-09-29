namespace StudioX.Application.Mcp;

/// <summary>源码路径的纯策略；只处理名称与目录规则，不执行文件读写。</summary>
internal static class McpWorkspacePathPolicy
{
    internal static bool IsWorkspaceWritableSourceFile(string? relative) =>
        IsWorkspaceSourceFile(relative) && relative is not null &&
        !IsUnderDeviceDirectory(relative) && ProjectFileService.CanCreateIn(relative);

    private static readonly HashSet<string> McpSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".s", ".asm", ".cmake", ".ld",
        ".v", ".vh", ".ve", ".sv", ".svh", ".py", ".pyi"
    };
    private static readonly HashSet<string> McpExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".studiox", ".build", "build", "bin", "obj",
        "toolsets", "node_modules", "packages", "artifacts", "secrets", "credentials"
    };

    internal static bool IsUnderDeviceDirectory(string relative) =>
        relative.Equals("device", StringComparison.OrdinalIgnoreCase) ||
        relative.StartsWith("device/", StringComparison.OrdinalIgnoreCase);

    internal static bool IsWorkspaceSourceDirectory(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 240 || relative.Contains('\\'))
        {
            return false;
        }
        return relative.Split('/').All(part => part.Length > 0 && !part.StartsWith('.') &&
            !McpExcludedDirectories.Contains(part) && !ContainsSensitiveWord(part) &&
            !part.StartsWith("cmake-build-", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsWorkspaceSourceFile(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 240 || relative.Contains('\\'))
        {
            return false;
        }
        var slash = relative.LastIndexOf('/');
        var parent = slash < 0 ? "" : relative[..slash];
        var name = relative[(slash + 1)..];
        if (parent.Length > 0 && !IsWorkspaceSourceDirectory(parent) || name.Length is 0 or > 120 ||
            name.StartsWith('.') || ContainsSensitiveWord(name))
        {
            return false;
        }
        return McpSourceExtensions.Contains(Path.GetExtension(name)) ||
            name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("sdkconfig", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("sdkconfig.defaults", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("sdkconfig.defaults.", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Kconfig", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Kconfig.projbuild", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("partitions.csv", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("idf_component.yml", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("README.md", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ContainsSensitiveWord(string part) =>
        new[] { "secret", "credential", "password", "token", "private" }
            .Any(word => part.Contains(word, StringComparison.OrdinalIgnoreCase));

}
