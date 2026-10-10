namespace StudioX.Application.Editing;

/// <summary>工程发现与变化监听共用产物排除规则；编译数据库等分析输入仍需被观察。</summary>
public static class ProjectChangePolicy
{
    private static readonly HashSet<string> generated = new([".git", ".studiox", ".build", "build", "bin", "obj", "node_modules", ".venv", "__pycache__"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> analysisFiles = new([".studiox/project.json", ".studiox/build.json", ".studiox/keil-import.json",
        ".studiox/espressif-module.json", ".studiox/toolchain.lock.json", ".studiox/development-components.lock.json",
        ".build/compile_commands.json", ".build/CMakeCache.txt", ".build/studiox-idf-runtime.json", ".build/project_description.json",
        ".build/config/sdkconfig.json", ".build/config/sdkconfig.h", ".build/toolchain/cflags", ".build/toolchain/cxxflags", ".build/toolchain/asmflags"], StringComparer.OrdinalIgnoreCase);

    public static bool ExcludesDirectory(string name) => generated.Contains(name) || name.StartsWith("cmake-build-", StringComparison.OrdinalIgnoreCase);

    public static bool Observes(string path)
    {
        if (analysisFiles.Contains(path))
        {
            return true;
        }
        return IsDiscoverable(path);
    }

    public static bool IsDiscoverable(string path) => path.Split('/').All(part => !ExcludesDirectory(part) && !part.StartsWith(".studiox-copy-", StringComparison.Ordinal) &&
            !part.StartsWith(".studiox-rename-", StringComparison.Ordinal) && !part.Contains(".tmp-", StringComparison.Ordinal));

    public static bool IsAnalysisInput(string path) => analysisFiles.Contains(path) || path.Equals("device/manifest.json", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals("CMakePresets.json", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals("sdkconfig", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).ToLowerInvariant() is ".c" or ".h" or ".cpp" or ".hpp" or ".hh" or ".cc" or ".cxx" or ".hxx" or ".s" or ".cmake" or ".rsp" ||
        Path.GetExtension(path).Length == 0;

    public static bool IsBuildInput(string path) => IsAnalysisInput(path) || Path.GetExtension(path).ToLowerInvariant() is
        ".asm" or ".inc" or ".ld" or ".lds" or ".sct" or ".a" or ".lib" or ".py" or ".v" or ".sv" or ".vh" or ".svh" or ".ve" or ".sdc" or ".cst" or ".pcf" or ".cfg" or ".json";
}
