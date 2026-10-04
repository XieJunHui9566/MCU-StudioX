namespace StudioX.Engine.Debugging;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Foundation;

internal static class DebugSourceStamp
{
    // 不把构建日志、IDE 设置或 Git 元数据计入源码；覆盖工程内源文件、头文件和构建脚本。
    internal static async Task<string> ComputeAsync(string root, CancellationToken token)
    {
        var projectPath = Path.Combine(root, ".studiox", "project.json");
        var espressif = File.Exists(projectPath) && (await ProjectService.ReadAsync(root, token)).Espressif is not null;
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                if (entry is DirectoryInfo)
                {
                    var excluded = espressif
                        ? entry.Name == ".git" || directory.Equals(root, StringComparison.OrdinalIgnoreCase) && entry.Name is ".build" or ".studiox"
                        : entry.Name is ".build" or ".git" or ".studiox" or "build" or "Build" or "Debug" or "Release";
                    if (!excluded)
                    {
                        pending.Push(entry.FullName);
                    }
                }
                // IDF 可以将任意后缀的字体、图片、证书或预编库嵌入固件，不能只按 C/C++ 扩展名过滤输入。
                else if (espressif || entry.Name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) ||
                    entry.Name is "sdkconfig" or "dependencies.lock" or "idf_component.yml" or "idf_component.yaml" ||
                    entry.Name.StartsWith("sdkconfig.defaults", StringComparison.Ordinal) ||
                    entry.Name.StartsWith("Kconfig", StringComparison.Ordinal) ||
                    new[] { ".c", ".h", ".s", ".cpp", ".cc", ".cxx", ".hpp", ".inc", ".ld", ".cmake", ".ioc" }.Contains(entry.Extension.ToLowerInvariant()))
                {
                    files.Add(entry.FullName);
                }
            }
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var componentLock = PathBoundary.Resolve(root, DevelopmentComponentLock.RelativePath);
        if (File.Exists(componentLock) || File.Exists(projectPath) && (await ProjectService.ReadAsync(root, token)).DevelopmentComponents is not null)
        {
            // 额外构建组件及声明变化也会改变二进制；旧工程尚无组件锁时维持原有凭据算法。
            foreach (var path in new[] { componentLock, PathBoundary.Resolve(root, "device/manifest.json") })
            {
                if (File.Exists(path))
                {
                    files.Add(path);
                }
            }
        }
        // 编译设置影响二进制；保存或外部编辑后不能把旧 ELF 当作当前工程的调试映像。
        var settings = Path.Combine(root, ProjectBuildSettings.RelativePath);
        if (File.Exists(settings))
        {
            files.Add(settings);
        }
        if (espressif)
        {
            var modulePath = Path.Combine(root, EspressifModuleSettings.RelativePath);
            if (File.Exists(modulePath))
            {
                files.Add(modulePath);
            }
            var module = await EspressifModuleConfiguration.ReadSettingsAsync(root, token);
            if (module.HasOverrides)
            {
                // 模块侧车和本次原生 SDK 使用的配置都影响二进制，不能因它们位于排除目录而漏掉。
                var config = Path.Combine(root, EspressifModuleSettings.GeneratedConfigPath);
                if (File.Exists(config))
                {
                    files.Add(config);
                }
                var nativeBase = await EspressifModuleSdkConfig.ReadBaseAsync(root, token);
                if (nativeBase is not null)
                {
                    // 原生配置可以位于 IDE/构建目录；基础配置及其路径记录也须绑定已有下载凭据。
                    files.Add(PathBoundary.Resolve(root, EspressifModuleSdkConfig.BaseRelativePath));
                    files.Add(nativeBase);
                }
            }
            var description = Path.Combine(root, ".build/project_description.json");
            if (File.Exists(description))
            {
                // 自定义 CMake 可以选择工程内其他配置文件；原生描述属于可重建缓存，损坏时交给配置阶段修复。
                try
                {
                    using var json = JsonDocument.Parse(await File.ReadAllTextAsync(description, token));
                    if (json.RootElement.TryGetProperty("project_path", out var projectDirectory) &&
                        projectDirectory.ValueKind == JsonValueKind.String && projectDirectory.GetString() is { } directory &&
                        EspressifPathIdentity.AreEqual(directory, root) && json.RootElement.TryGetProperty("config_file", out var actual) &&
                        actual.ValueKind == JsonValueKind.String && actual.GetString() is { } actualPath)
                    {
                        var canonical = EspressifPathIdentity.NormalizePath(Path.GetFullPath(actualPath, root));
                        var relative = Path.GetRelativePath(EspressifPathIdentity.NormalizePath(root), canonical).Replace('\\', '/');
                        if (!relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathFullyQualified(relative))
                        {
                            var config = PathBoundary.Resolve(root, relative);
                            if (File.Exists(config))
                            {
                                files.Add(config);
                            }
                        }
                    }
                }
                catch (JsonException) { /* 原生配置重新生成描述；损坏的描述仍会被下载布局的严格解析拒绝。 */ }
            }
        }
        var stcIspPath = Path.Combine(root, StcIspSettings.RelativePath);
        StcIspSettings? stcIsp = File.Exists(stcIspPath) ? await StcIspSettings.ReadAsync(root, token) : null;
        if (stcIsp is { ClockMode: not StcClockMode.Preserve })
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"stc-clock\0{stcIsp.ClockMode}:{stcIsp.ClockFrequencyHz}\0"));
        }
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file).Replace('\\', '/') + "\0"));
            await using var stream = File.OpenRead(file);
            hash.AppendData(await SHA256.HashDataAsync(stream, token));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
