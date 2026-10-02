namespace StudioX.Engine;

using StudioX.Foundation;

/// <summary>每次构建显式绑定已校验的 SDK、Python 与工具目录，避免读取另一套系统 IDF。</summary>
internal static class EspressifBuildEnvironment
{
    internal static async Task<Dictionary<string, string>> CreateAsync(string projectRoot, ResolvedToolset tools,
        EspressifProjectSettings settings, CancellationToken token)
    {
        if (tools.Manifest.Purpose != settings.Framework || tools.Manifest.Version != settings.SdkVersion)
        {
            throw new StudioXException("ESPRESSIF_TOOLSET", "工程的 SDK 身份与内置工具集不一致。");
        }
        var environment = ToolsetEnvironment.Create(tools);
        var state = PathBoundary.Resolve(projectRoot, ".build/idf-tools-state");
        Directory.CreateDirectory(state);
        foreach (var file in Directory.EnumerateFiles(tools.ResourceDirectory("tools"), "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (name == "idf-env.json" || name.StartsWith("espidf.constraints.", StringComparison.Ordinal) && name.EndsWith(".txt", StringComparison.Ordinal))
            {
                await File.WriteAllBytesAsync(PathBoundary.Resolve(state, name), await File.ReadAllBytesAsync(file, token), token);
            }
        }
        environment["IDF_PATH"] = EspressifNativePath.For(tools.ResourceDirectory("idf"));
        environment["IDF_TOOLS_PATH"] = EspressifNativePath.For(state);
        environment["IDF_PYTHON_ENV_PATH"] = EspressifNativePath.For(tools.ResourceDirectory("python-env"));
        environment["PYTHONHOME"] = environment["IDF_PYTHON_ENV_PATH"];
        environment["IDF_TARGET"] = settings.Target;
        environment["ESP_IDF_VERSION"] = settings.Framework == "esp-idf" ? settings.SdkVersion : "3.4";
        environment["PYTHON"] = EspressifNativePath.ForExecutable(tools.Tool("python"));
        environment["PATH"] = string.Join(Path.PathSeparator, tools.Manifest.Executables.Keys
            .Select(role => EspressifNativePath.For(Path.GetDirectoryName(tools.Tool(role))!))
            .Distinct(StringComparer.OrdinalIgnoreCase).Append(Environment.GetFolderPath(Environment.SpecialFolder.System)));
        environment["PYTHONUTF8"] = "1";
        environment["IDF_CCACHE_ENABLE"] = "0";
        // 编译生成物和 Python 缓存不写入共享 SDK；发行哈希在下次构建时仍可复用。
        environment["PYTHONDONTWRITEBYTECODE"] = "1";
        return environment;
    }
}
