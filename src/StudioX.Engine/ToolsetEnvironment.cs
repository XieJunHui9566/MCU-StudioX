namespace StudioX.Engine;

public static class ToolsetEnvironment
{
    // 开发机上已有的 GCC/CMake/Python 配置不能改变内置工具实际使用的程序和标准库。
    public static readonly string[] AmbientVariables = ["GCC_EXEC_PREFIX", "COMPILER_PATH", "LIBRARY_PATH", "CPATH", "C_INCLUDE_PATH", "CPLUS_INCLUDE_PATH", "OBJC_INCLUDE_PATH", "CC", "CXX", "AS", "AR", "LD", "CFLAGS", "CXXFLAGS", "CPPFLAGS", "LDFLAGS", "ASMFLAGS", "CMAKE_TOOLCHAIN_FILE", "CMAKE_GENERATOR", "CMAKE_GENERATOR_PLATFORM", "CMAKE_GENERATOR_TOOLSET", "CMAKE_PREFIX_PATH", "CMAKE_PROGRAM_PATH", "CMAKE_INCLUDE_PATH", "CMAKE_LIBRARY_PATH", "PYTHONHOME", "PYTHONPATH", "PYTHONPYCACHEPREFIX", "OPENOCD_SCRIPTS"];
    public static Dictionary<string, string> Create(ResolvedToolset tools) => new()
    {
        ["PATH"] = string.Join(Path.PathSeparator, tools.Manifest.Executables.Keys.Select(role => Path.GetDirectoryName(tools.Tool(role))!).Distinct(StringComparer.OrdinalIgnoreCase)
            .Append(Environment.GetFolderPath(Environment.SpecialFolder.System))),
        ["PYTHONDONTWRITEBYTECODE"] = "1", ["PYTHONNOUSERSITE"] = "1",
        // GDB 内嵌 Python 即使忽略禁写标记，也只能将缓存写入本次进程的临时目录；
        // 同时不读取工具目录中既有的、未参与发行哈希的 __pycache__。
        ["PYTHONPYCACHEPREFIX"] = Path.Combine(Path.GetTempPath(), "MCU-StudioX", "python-cache", Guid.NewGuid().ToString("N"))
    };
}
