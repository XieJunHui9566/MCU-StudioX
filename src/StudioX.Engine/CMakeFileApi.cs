namespace StudioX.Engine;

using System.Text.Json;
using StudioX.Foundation;

/// <summary>读取 CMake 实际生成的目标和工具链，避免从 CMake 脚本文本推测产物名称。</summary>
internal static class CMakeFileApi
{
    public static async Task QueryAsync(string build, CancellationToken token)
    {
        var query = Path.Combine(build, ".cmake/api/v1/query/client-studiox");
        Directory.CreateDirectory(query);
        foreach (var name in new[] { "codemodel-v2", "toolchains-v1" })
            await File.WriteAllTextAsync(Path.Combine(query, name), "", token);
    }

    public static async Task<string[]> ExecutablesAsync(string build, ResolvedToolset tools, CancellationToken token)
    {
        var reply = Path.Combine(build, ".cmake/api/v1/reply");
        var index = new DirectoryInfo(reply).EnumerateFiles("index-*.json").OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault()
            ?? throw new StudioXException("CMAKE_REPLY", "CMake 未生成工程信息，请查看配置日志。");
        using var document = await ReadAsync(index.FullName, token);
        var answers = document.RootElement.GetProperty("reply").GetProperty("client-studiox");
        using var toolchains = await ReadAsync(PathBoundary.Resolve(reply, answers.GetProperty("toolchains-v1").GetProperty("jsonFile").GetString()!), token);
        foreach (var chain in toolchains.RootElement.GetProperty("toolchains").EnumerateArray())
        {
            var language = chain.GetProperty("language").GetString();
            if (language is not ("C" or "CXX" or "ASM")) continue;
            var compiler = chain.GetProperty("compiler").GetProperty("path").GetString()!;
            if (!Path.GetFullPath(compiler).Equals(Path.GetFullPath(tools.Tool(language == "CXX" ? "gxx" : "gcc")), StringComparison.OrdinalIgnoreCase))
                throw new StudioXException("CUBEMX_COMPILER", "工程指定了其他编译器：" + compiler + "。请将 CubeMX GCC 工具链改为通过 arm-none-eabi- 前缀查找内置编译器。");
        }
        using var model = await ReadAsync(PathBoundary.Resolve(reply, answers.GetProperty("codemodel-v2").GetProperty("jsonFile").GetString()!), token);
        var artifacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in model.RootElement.GetProperty("configurations").EnumerateArray())
        foreach (var reference in config.GetProperty("targets").EnumerateArray())
        {
            using var target = await ReadAsync(PathBoundary.Resolve(reply, reference.GetProperty("jsonFile").GetString()!), token);
            if (target.RootElement.GetProperty("type").GetString() != "EXECUTABLE") continue;
            foreach (var artifact in target.RootElement.GetProperty("artifacts").EnumerateArray())
            {
                var path = artifact.GetProperty("path").GetString()!;
                // 产物必须归当前构建目录所有，避免转换固件时写入源码或外部路径。
                artifacts.Add(PathBoundary.Resolve(build, Path.GetRelativePath(build, Path.GetFullPath(path, build)).Replace('\\', '/')));
            }
        }
        if (artifacts.Count == 0) throw new StudioXException("CUBEMX_TARGET", "CMake 工程没有可执行固件目标。");
        return artifacts.ToArray();
    }

    private static async Task<JsonDocument> ReadAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token);
    }
}
