namespace StudioX.Engine;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

/// <summary>旧 Xtensa/binutils 不支持 Unicode 路径；每个工程复用一个 ASCII 执行目录，校验后发布实际产物。</summary>
internal static class EspressifProjectWorkspace
{
    internal static async Task<BuildReport> ExecuteAsync(string dataDirectory, string root, ProjectManifest project, ResolvedToolset tools,
        ProjectBuildSettings settings, bool configureOnly, IProgress<string>? progress, IProgress<string>? output, CancellationToken token,
        Func<string, ProjectManifest, ResolvedToolset, ProjectBuildSettings, bool, IProgress<string>?, IProgress<string>?, CancellationToken, Task<BuildReport>> execute)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())))[..20];
        var owner = Path.Combine(dataDirectory, "esp", key);
        Directory.CreateDirectory(owner);
        using var lease = ToolUsageLease.Acquire(owner, maintenance: true);
        var stage = PathBoundary.Resolve(owner, "project");
        Directory.CreateDirectory(stage);
        var native = EspressifNativePath.For(stage);
        var stamp = await DebugSourceStamp.ComputeAsync(root, token);
        var inputs = SourceFiles(root).ToArray();
        var manifest = PathBoundary.Resolve(owner, "inputs.json");
        if (File.Exists(manifest))
        {
            foreach (var old in await JsonStore.ReadAsync<string[]>(manifest, token))
            {
                if (old.StartsWith(".build/", StringComparison.OrdinalIgnoreCase) || old.StartsWith(".git/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new StudioXException("ESPRESSIF_WORKSPACE", "私有构建输入清单含有保留路径。");
                }
                if (!inputs.Contains(old, StringComparer.Ordinal))
                {
                    File.Delete(PathBoundary.Resolve(stage, old));
                }
            }
        }
        foreach (var relative in inputs)
        {
            await CopyChangedAsync(root, stage, relative, token);
        }
        // 编译选项侧车位于 .build，属于输入而不是可复用的原生 CMake 缓存。
        foreach (var relative in new[] { EspressifModuleSettings.GeneratedConfigPath, EspressifModuleSdkConfig.BaseRelativePath })
        {
            if (File.Exists(PathBoundary.Resolve(root, relative)))
            {
                await CopyChangedAsync(root, stage, relative, token);
            }
        }
        if (await EspressifModuleSdkConfig.ReadBaseAsync(root, token) is { } nativeBase)
        {
            await CopyChangedAsync(root, stage, Path.GetRelativePath(root, nativeBase).Replace('\\', '/'), token);
        }
        var baseRecord = PathBoundary.Resolve(stage, EspressifModuleSdkConfig.BaseRelativePath);
        if (File.Exists(baseRecord))
        {
            using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(baseRecord, token));
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                Rewrite(json.RootElement, writer, text => text.Replace(root.Replace('\\', '/'), stage.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                    .Replace(root, stage, StringComparison.OrdinalIgnoreCase));
            }
            await File.WriteAllBytesAsync(baseRecord, buffer.ToArray(), token);
        }
        if (stamp != await DebugSourceStamp.ComputeAsync(root, token) || stamp != await DebugSourceStamp.ComputeAsync(stage, token))
        {
            throw new StudioXException("ESPRESSIF_BUILD_CHANGED", "准备私有构建目录期间工程发生变化，请重新编译。");
        }
        string Display(string text)
        {
            foreach (var prefix in new[] { native, stage }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                text = text.Replace(prefix.Replace('\\', '/'), root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                    .Replace(prefix, root, StringComparison.OrdinalIgnoreCase);
            }
            return text;
        }
        var initial = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relative in ProjectSourceSnapshot.Files(stage))
        {
            initial.Add(relative, await HashAsync(PathBoundary.Resolve(stage, relative), token));
        }
        try
        {
            File.Delete(PathBoundary.Resolve(stage, BuildReceipt.RelativePath));
            progress?.Report("在兼容目录构建，工程名称与源码路径保持中文。");
            var report = await execute(stage, project, tools, settings, configureOnly, progress,
                output is null ? null : new MappedOutput(text => output.Report(Display(text))), token);
            if (stamp != await DebugSourceStamp.ComputeAsync(root, token))
            {
                throw new StudioXException("ESPRESSIF_BUILD_CHANGED", "编译期间原工程发生变化，未发布产物，请重新编译。");
            }
            if (report.Success)
            {
                foreach (var relative in ProjectSourceSnapshot.Files(stage))
                {
                    if (initial.TryGetValue(relative, out var before) && before == await HashAsync(PathBoundary.Resolve(stage, relative), token))
                    {
                        continue;
                    }
                    if (relative is not ("sdkconfig" or "sdkconfig.old" or "dependencies.lock") && !relative.StartsWith("managed_components/", StringComparison.Ordinal))
                    {
                        throw new StudioXException("ESPRESSIF_BUILD_CHANGED", "SDK 修改了非生成输入，未覆盖原工程：" + relative);
                    }
                    var original = PathBoundary.Resolve(root, relative);
                    if (File.Exists(original) && (!initial.TryGetValue(relative, out var expected) || expected != await HashAsync(original, token)))
                    {
                        throw new StudioXException("ESPRESSIF_BUILD_CHANGED", "发布 SDK 配置前原文件发生变化，未覆盖：" + relative);
                    }
                    await CopyChangedAsync(stage, root, relative, token);
                }
            }
            var build = PathBoundary.Resolve(root, ".build");
            Directory.CreateDirectory(build);
            var log = Display(report.Log);
            var logPath = PathBoundary.Resolve(build, "studiox-build.log");
            await File.WriteAllTextAsync(logPath, log, token);
            if (!report.Success)
            {
                return report with
                {
                    Log = log,
                    LogPath = logPath,
                    Artifacts = []
                };
            }

            var stageBuild = PathBoundary.Resolve(stage, ".build");
            // 复制完整的最新输出；私有 CMake/Ninja 缓存留在固定目录，下一次构建仍可增量复用。
            foreach (var relative in ToolsetSnapshot.Capture(stageBuild, token).Files)
            {
                if (relative == "studiox-build-receipt.json")
                {
                    continue;
                }
                await CopyChangedAsync(stageBuild, build, relative, token);
                if (Path.GetExtension(relative) == ".json")
                {
                    var path = PathBoundary.Resolve(build, relative);
                    using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(path, token));
                    using var buffer = new MemoryStream();
                    var changed = false;
                    using (var writer = new Utf8JsonWriter(buffer))
                    {
                        Rewrite(json.RootElement, writer, text =>
                        {
                            var mapped = Display(text);
                            changed |= mapped != text;
                            return mapped;
                        });
                    }
                    if (changed)
                    {
                        await File.WriteAllBytesAsync(path, buffer.ToArray(), token);
                    }
                }
            }
            await File.WriteAllTextAsync(logPath, log, token);
            if (!configureOnly)
            {
                var receipt = await JsonStore.ReadAsync<BuildReceipt>(PathBoundary.Resolve(stage, BuildReceipt.RelativePath), token);
                var compiledStamp = await DebugSourceStamp.ComputeAsync(stage, token);
                if (receipt.SourceStamp != compiledStamp || compiledStamp != await DebugSourceStamp.ComputeAsync(root, token) ||
                    project != await ProjectService.ReadAsync(root, token))
                {
                    throw new StudioXException("ESPRESSIF_BUILD_CHANGED", "发布前源码或构建配置已变化，旧固件未获得下载凭据。");
                }
                await JsonStore.WriteAsync(PathBoundary.Resolve(root, BuildReceipt.RelativePath), receipt with
                {
                    SourceStamp = compiledStamp,
                    EspressifLayout = await EspressifFlashLayoutReader.ParseAsync(root, ".build/flasher_args.json", project.Espressif!.Target, token: token)
                }, token);
            }
            return report with
            {
                Log = log,
                LogPath = logPath,
                Artifacts = report.Artifacts.Select(Display).ToArray()
            };
        }
        catch (Exception error)
        {
            File.Delete(PathBoundary.Resolve(root, BuildReceipt.RelativePath));
            await File.AppendAllTextAsync(PathBoundary.Resolve(root, ".build/studiox-build.log"),
                "\n兼容构建或产物发布失败：\n" + Display(error.ToString()), CancellationToken.None);
            throw;
        }
        finally
        {
            try
            {
                await JsonStore.WriteAsync(manifest, SourceFiles(stage).ToArray(), CancellationToken.None);
            }
            catch
            {
                File.Delete(PathBoundary.Resolve(root, BuildReceipt.RelativePath));
                throw;
            }
        }
    }

    private static IEnumerable<string> SourceFiles(string root) => ProjectSourceSnapshot.Files(root).Concat(
        Directory.Exists(Path.Combine(root, ".studiox"))
            ? ProjectSourceSnapshot.Files(Path.Combine(root, ".studiox")).Select(path => ".studiox/" + path) : []);

    private static async Task CopyChangedAsync(string source, string destination, string relative, CancellationToken token)
    {
        var input = PathBoundary.Resolve(source, relative);
        var target = PathBoundary.Resolve(destination, relative);
        if (File.Exists(target) && new FileInfo(input).Length == new FileInfo(target).Length && await HashAsync(input, token) == await HashAsync(target, token))
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var read = File.OpenRead(input);
        await using var write = File.Create(target);
        await read.CopyToAsync(write, token);
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token));
    }

    private static void Rewrite(JsonElement value, Utf8JsonWriter writer, Func<string, string> display)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    Rewrite(property.Value, writer, display);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    Rewrite(item, writer, display);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(display(value.GetString()!));
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private sealed class MappedOutput(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
