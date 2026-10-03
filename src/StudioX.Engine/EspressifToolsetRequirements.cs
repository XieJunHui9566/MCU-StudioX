namespace StudioX.Engine;

using StudioX.Foundation;

/// <summary>原生 IDF 运行依赖的清单约束；缺失时不退回开发机上的工具。</summary>
internal static class EspressifToolsetRequirements
{
    internal static string[] RequiredRoles(string framework)
    {
        var compilers = framework == "esp-idf" ? new[] { "esp32", "esp32s3", "riscv" } : ["esp8266"];
        return new[] { "python", "cmake", "ninja", "git" }
            .Concat(compilers.SelectMany(target => new[] { "gcc-" + target, "gxx-" + target, "size-" + target }))
            .ToArray();
    }

    internal static void ValidateResources(ToolsetManifest manifest, string root)
    {
        foreach (var role in new[] { "idf", "tools", "python-env" })
        {
            if (manifest.ResourceDirectories is null || !manifest.ResourceDirectories.TryGetValue(role, out var relative) ||
                !Directory.Exists(PathBoundary.Resolve(root, relative)))
            {
                throw new StudioXException("TOOL_RESOURCE", "Espressif 开发环境组件缺少资源目录：" + role);
            }
        }
        var sdk = PathBoundary.Resolve(root, manifest.ResourceDirectories!["idf"]);
        if (!File.Exists(PathBoundary.Resolve(sdk, "tools/idf.py")) || !File.Exists(PathBoundary.Resolve(sdk, "tools/cmake/project.cmake")))
        {
            throw new StudioXException("TOOL_RESOURCE", "Espressif 开发环境组件缺少原生 IDF 构建入口。");
        }
    }
}
