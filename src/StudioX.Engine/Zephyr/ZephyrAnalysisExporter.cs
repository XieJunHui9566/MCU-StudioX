namespace StudioX.Engine.Zephyr;

using System.Text.Json;
using StudioX.Foundation;

/// <summary>将临时快照的实际编译参数映射回工程，并保留生成头文件供编辑器使用。</summary>
internal static class ZephyrAnalysisExporter
{
    internal static async Task ExportAsync(string root, string source, string build, CancellationToken token)
    {
        var destination = PathBoundary.Resolve(root, ".build/zephyr/native");
        Directory.CreateDirectory(destination);
        var generated = Path.Combine(build, "zephyr/include/generated");
        if (Directory.Exists(generated))
        {
            foreach (var file in Directory.EnumerateFiles(generated, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                var target = PathBoundary.Resolve(destination, "zephyr/include/generated/" + Path.GetRelativePath(generated, file).Replace('\\', '/'));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
        }
        var database = Path.Combine(build, "compile_commands.json");
        if (!File.Exists(database))
        {
            return;
        }
        var nativeSource = ZephyrNativePath.Resolve(source);
        var nativeBuild = ZephyrNativePath.Resolve(build);
        string Map(string value) => value.Replace(nativeSource, root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
            .Replace(nativeBuild, destination.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
            .Replace(nativeSource.Replace('/', '\\'), root, StringComparison.OrdinalIgnoreCase)
            .Replace(nativeBuild.Replace('/', '\\'), destination, StringComparison.OrdinalIgnoreCase)
            .Replace(source.Replace('\\', '/'), root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
            .Replace(build.Replace('\\', '/'), destination.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
            .Replace(source, root, StringComparison.OrdinalIgnoreCase)
            .Replace(build, destination, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(database, token));
        var commands = new List<object>();
        foreach (var entry in json.RootElement.EnumerateArray())
        {
            var originalFile = entry.GetProperty("file").GetString()!.Replace('\\', '/');
            if (!originalFile.StartsWith(nativeSource + "/", StringComparison.OrdinalIgnoreCase) &&
                !originalFile.StartsWith(source.Replace('\\', '/') + "/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var file = Map(originalFile);
            var arguments = entry.TryGetProperty("arguments", out var args) ? args.EnumerateArray().Select(a => a.GetString()!).ToArray()
                : CMakeCommandLine.Split(entry.GetProperty("command").GetString()!);
            commands.Add(new
            {
                directory = destination,
                file,
                arguments = arguments.Select(Map).ToArray()
            });
        }
        await JsonStore.WriteAsync(PathBoundary.Resolve(root, ".build/compile_commands.json"), commands, token);
    }
}
