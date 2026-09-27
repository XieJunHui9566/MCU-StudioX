namespace StudioX.Engine;

using System.Text.RegularExpressions;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>复制原生示例并只锁定工程名称、目标和已核验的模块容量。</summary>
internal static class EspressifExampleScaffold
{
    internal static async Task WriteAsync(string directory, BuildPlan plan, ProjectTemplate template, CancellationToken token)
    {
        var example = template.EspressifExample!;
        var source = PathBoundary.Resolve(Path.Combine(directory, "device"), example.ExampleDirectory);
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, path).Replace('\\', '/');
            var destination = PathBoundary.Resolve(directory, relative);
            if (File.Exists(destination))
            {
                throw new StudioXException("PROJECT_RESERVED_FILE", "原生 SDK 示例与工程元数据路径冲突：" + relative);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(PathBoundary.Resolve(source, relative), destination);
        }
        var rootFile = PathBoundary.Resolve(directory, "CMakeLists.txt");
        var root = await File.ReadAllTextAsync(rootFile, token);
        var projectCall = new Regex(@"\bproject\((?<name>[A-Za-z0-9_]+)\)", RegexOptions.CultureInvariant);
        const string include = "include($ENV{IDF_PATH}/tools/cmake/project.cmake)";
        if (projectCall.Matches(root).Count != 1 || root.IndexOf(include, StringComparison.Ordinal) != root.LastIndexOf(include, StringComparison.Ordinal) ||
            !root.Contains(include, StringComparison.Ordinal))
        {
            throw new StudioXException("PROJECT_ESPRESSIF_EXAMPLE", "SDK 示例根 CMake 缺少唯一的原生入口或工程名称。");
        }
        root = projectCall.Replace(root, "project(" + plan.Project.Name + ")");
        // 官方 MINIMAL_BUILD、组件依赖和原始文件名全部保留；仅增加不可猜测的目标锁定。
        root = root.Replace(include, $"set(IDF_TARGET \"{plan.Project.Espressif!.Target}\" CACHE STRING \"StudioX locked SDK target\")\n\n" + include, StringComparison.Ordinal);
        await File.WriteAllTextAsync(rootFile, root, token);
        var defaultsPath = PathBoundary.Resolve(directory, "sdkconfig.defaults");
        var defaults = File.Exists(defaultsPath) ? await File.ReadAllTextAsync(defaultsPath, token) : "";
        if (defaults.Length > 0 && !defaults.EndsWith('\n'))
        {
            defaults += "\n";
        }
        defaults += $"CONFIG_IDF_TARGET=\"{plan.Project.Espressif!.Target}\"\n";
        if (plan.Device.Id == "ESP32-WROOM-32")
        {
            defaults += "CONFIG_ESPTOOLPY_FLASHSIZE_4MB=y\n";
        }
        await File.WriteAllTextAsync(defaultsPath, defaults, token);
    }
}
