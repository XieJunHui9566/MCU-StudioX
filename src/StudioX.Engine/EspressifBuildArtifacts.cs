namespace StudioX.Engine;

using System.Text.Json;
using StudioX.Foundation;

/// <summary>从原生构建描述确认应用目标，不从工程名称猜测 BIN 或 ELF 文件。</summary>
internal sealed record EspressifBuildArtifacts(string ApplicationBin, string ApplicationElf, string[] Artifacts)
{
    internal static async Task<EspressifBuildArtifacts> ReadAsync(string root, ResolvedToolset tools,
        EspressifProjectSettings settings, bool requireArtifacts, CancellationToken token)
    {
        var build = PathBoundary.Resolve(root, ".build");
        var descriptionPath = PathBoundary.Resolve(build, "project_description.json");
        using var description = JsonDocument.Parse(await File.ReadAllTextAsync(descriptionPath, token));
        var value = description.RootElement;
        string Required(string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(field.GetString()) ? field.GetString()!
            : throw new StudioXException("ESPRESSIF_DESCRIPTION", "IDF 构建描述缺少字段：" + name);
        if (settings.Framework == "esp-idf" && Required("target") != settings.Target)
        {
            throw new StudioXException("ESPRESSIF_TARGET", "原生构建目标与工程锁定的芯片不一致。");
        }
        var expectedPaths = new List<(string Field, string Expected)> { ("project_path", root), ("build_dir", build) };
        if (settings.Framework == "esp-idf")
        {
            expectedPaths.AddRange([("idf_path", tools.ResourceDirectory("idf")), ("c_compiler", tools.ForEspressifTarget(settings.Target).Tool("gcc"))]);
        }
        else
        {
            // ESP8266 3.4 的构建描述没有 SDK、目标和编译器字段，改从原生 CMake 缓存核对。
            var cache = (await File.ReadAllLinesAsync(PathBoundary.Resolve(build, "CMakeCache.txt"), token))
                .Where(line => !line.StartsWith("//", StringComparison.Ordinal) && !line.StartsWith('#') && line.Contains(':') && line.Contains('='))
                .Select(line => line.Split('=', 2)).ToDictionary(pair => pair[0].Split(':', 2)[0], pair => pair[1], StringComparer.Ordinal);
            if (!cache.TryGetValue("IDF_TARGET", out var target) || target != settings.Target ||
                !cache.TryGetValue("CMAKE_TOOLCHAIN_FILE", out var toolchain) ||
                !EspressifPathIdentity.AreEqual(toolchain, PathBoundary.Resolve(tools.ResourceDirectory("idf"), "tools/cmake/toolchain-esp8266.cmake")))
            {
                throw new StudioXException("ESPRESSIF_DESCRIPTION", "ESP8266 原生缓存的目标、编译器或 SDK 工具链与工程不一致。");
            }
            // 旧 SDK 用普通变量设置编译器，CMakeCache 可能没有 compiler 项；File API 才是实际工具链记录。
            _ = await CMakeFileApi.ExecutablesAsync(build, tools.ForEspressifTarget(settings.Target), token);
        }
        foreach (var (field, expected) in expectedPaths)
        {
            if (!EspressifPathIdentity.AreEqual(Path.GetFullPath(Required(field), build), expected))
            {
                throw new StudioXException("ESPRESSIF_DESCRIPTION", "IDF 构建描述引用了其他工程或工具（" + field + "）：" + Required(field));
            }
        }
        string Artifact(string field)
        {
            var absolute = EspressifPathIdentity.NormalizePath(Path.GetFullPath(Required(field), build));
            return PathBoundary.Resolve(build, Path.GetRelativePath(build, absolute).Replace('\\', '/'));
        }
        var bin = Artifact("app_bin");
        var elf = Artifact("app_elf");
        if (Path.GetExtension(bin) != ".bin" || Path.GetExtension(elf) != ".elf")
        {
            throw new StudioXException("ESPRESSIF_ARTIFACT", "IDF 应用映像或符号文件扩展名无效。");
        }
        var candidates = new[] { bin, elf, Path.ChangeExtension(elf, ".map"), descriptionPath,
            PathBoundary.Resolve(build, "compile_commands.json"), PathBoundary.Resolve(build, "flasher_args.json") };
        if (requireArtifacts && candidates.Any(path => !File.Exists(path)))
        {
            throw new StudioXException("ESPRESSIF_ARTIFACT", "IDF 报告成功，但应用映像、符号、映射或下载参数缺失。");
        }
        return new(bin, elf, candidates.Where(File.Exists).ToArray());
    }
}
