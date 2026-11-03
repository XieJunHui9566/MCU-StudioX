namespace StudioX.Engine.Pic;

using StudioX.Foundation;

/// <summary>同一份 CMake 入口供 IDE 与独立命令行使用，XC8 只负责目标编译和链接。</summary>
public static class PicCMake
{
    public const string ToolsetId = "studiox.cmake";
    public const string Version = "4.4.0";
    public const string CompilerId = "cmake-ninja";

    public static async Task EnsureAsync(string root, ProjectManifest project, CancellationToken token = default)
    {
        var entry = PathBoundary.Resolve(root, "CMakeLists.txt");
        if (File.Exists(entry))
        {
            return;
        }
        var sources = ProjectSourceSnapshot.Files(root).Where(file => file.StartsWith("src/", StringComparison.Ordinal) &&
            Path.GetExtension(file).Equals(".c", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToArray();
        if (sources.Length == 0 || sources.Any(file => file.Any(c => c is '"' or '$' or ';' or '\r' or '\n')))
        {
            throw new StudioXException("PIC_CMAKE_SOURCE", "创建 PIC CMake 工程需要有效的 C 源码路径。");
        }
        var module = PathBoundary.Resolve(root, "cmake/PicXc8.cmake");
        Directory.CreateDirectory(Path.GetDirectoryName(module)!);
        if (!File.Exists(module))
        {
            using var stream = typeof(PicCMake).Assembly.GetManifestResourceStream("StudioX.Engine.Pic.PicXc8.cmake")
                ?? throw new InvalidOperationException("Missing PIC CMake resource.");
            using var reader = new StreamReader(stream);
            await File.WriteAllTextAsync(module, await reader.ReadToEndAsync(token), token);
        }
        var text = "cmake_minimum_required(VERSION 3.24)\nproject(PicFirmware LANGUAGES NONE)\n" +
            "set(PIC_MCU \"" + project.Pic!.CompilerTarget + "\")\ninclude(cmake/PicXc8.cmake)\n" +
            "add_library(app INTERFACE)\ntarget_sources(app INTERFACE\n" +
            string.Join("\n", sources.Select(file => "  \"" + file + "\"")) +
            "\n)\ntarget_include_directories(app INTERFACE \"${CMAKE_CURRENT_SOURCE_DIR}/include\" \"${CMAKE_CURRENT_SOURCE_DIR}/src\")\n" +
            "pic_xc8_firmware(app)\n";
        // 创建阶段登记模板源码，此后只读取用户维护的 CMake 列表。
        await File.WriteAllTextAsync(entry, text, token);
    }
}
