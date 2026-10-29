namespace StudioX.Engine.Arduino;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Foundation;

/// <summary>从经校验的固定工具组件建立私有 CLI 环境；不会读取系统 Arduino 配置或 PATH。</summary>
public sealed class ArduinoRuntime(ToolsetCatalog catalog, string dataDirectory)
{
    private readonly ConcurrentDictionary<string, ToolsetSnapshot> verifiedCopies = new(StringComparer.OrdinalIgnoreCase);
    public string LibraryRoot(ArduinoProjectSettings settings)
    {
        var identity = string.Join('\n', settings.Libraries.OrderBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => item.Name + "@" + item.Version));
        return PathBoundary.Resolve(dataDirectory, "arduino/l/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20]);
    }

    internal Task<Execution> OpenAsync(ArduinoProjectSettings settings, CancellationToken token, IProgress<string>? output = null) =>
        Task.Run(() => OpenCoreAsync(settings, token, output), token);

    private async Task<Execution> OpenCoreAsync(ArduinoProjectSettings settings, CancellationToken token, IProgress<string>? output)
    {
        settings.Validate();
        var platform = settings.Platform;
        var tools = await catalog.ResolveAsync(platform.ToolsetId, platform.CoreVersion, platform.CompilerId, token, progress: output);
        if (tools.Manifest.Purpose != "arduino" || tools.Manifest.ComponentVersions?.GetValueOrDefault("arduino-cli") != ArduinoProjectSettings.CliVersion ||
            tools.Manifest.ComponentVersions.GetValueOrDefault(platform.CoreId) != platform.CoreVersion)
        {
            throw new StudioXException("ARDUINO_RUNTIME", "Arduino 工具组件缺少锁定的 CLI / 核心版本：" + platform.DisplayName);
        }
        var use = ToolUsageLease.Acquire(tools.RootDirectory);
        ToolUsageLease? cacheLease = null;
        try
        {
            // AVR GCC 7 使用传统 Windows 路径 API；摘要缩短目录，完整内容仍逐项核验。
            var cache = PathBoundary.Resolve(dataDirectory, "arduino/r/" + tools.Fingerprint[..20]);
            Directory.CreateDirectory(cache);
            // CLI 会写索引与缓存；固定工具包只读，所有写入发生在独立用户数据目录。
            cacheLease = ToolUsageLease.Acquire(cache, maintenance: true);
            var seed = tools.ResourceDirectory("arduino-seed");
            var data = PathBoundary.Resolve(cache, "data");
            if (!Directory.Exists(data))
            {
                var staging = PathBoundary.Resolve(cache, "seed-" + Guid.NewGuid().ToString("N"));
                output?.Report("首次准备 " + platform.DisplayName + " 私有核心和工具副本…");
                await CopyAsync(seed, staging, token, output);
                Directory.Move(staging, data);
            }
            var packages = PathBoundary.Resolve(data, "packages");
            var before = ToolsetSnapshot.Capture(packages, token);
            if (verifiedCopies.TryGetValue(packages, out var known) && known.Matches(before))
            {
                output?.Report("Arduino 私有工具副本未变化，复用本次启动的完整校验结果。");
                return new Execution(tools, cache, data, LibraryRoot(settings), use, cacheLease);
            }
            verifiedCopies.TryRemove(packages, out _);
            output?.Report("完整校验 Arduino 私有工具副本…");
            var completed = 0;
            var last = Stopwatch.GetTimestamp();
            foreach (var (relative, expected) in tools.Manifest.Sha256.Where(item => item.Key.StartsWith("seed/packages/", StringComparison.Ordinal)))
            {
                await using var file = File.OpenRead(PathBoundary.Resolve(data, relative[5..]));
                if (!Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new StudioXException("ARDUINO_CACHE_CHANGED", "Arduino 私有工具内容变化，请删除此版本缓存后从固定组件重新恢复：" + relative);
                }
                completed++;
                if (output is not null && Stopwatch.GetElapsedTime(last).TotalMilliseconds >= 500)
                {
                    output.Report("校验 Arduino 私有工具副本… " + completed.ToString("N0") + " 个文件");
                    last = Stopwatch.GetTimestamp();
                }
            }
            var expectedFiles = tools.Manifest.Sha256.Keys.Where(path => path.StartsWith("seed/packages/", StringComparison.Ordinal))
                .Select(path => path[14..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var after = ToolsetSnapshot.Capture(packages, token);
            if (!before.Matches(after) || !expectedFiles.SetEquals(after.Files))
            {
                throw new StudioXException("ARDUINO_CACHE_CHANGED", "Arduino 私有工具目录包含未锁定文件，拒绝编译或上传。");
            }
            token.ThrowIfCancellationRequested();
            verifiedCopies[packages] = after;
            return new Execution(tools, cache, data, LibraryRoot(settings), use, cacheLease);
        }
        catch
        {
            cacheLease?.Dispose();
            use.Dispose();
            throw;
        }
    }

    internal static async Task CopyAsync(string source, string target, CancellationToken token, IProgress<string>? output = null)
    {
        Directory.CreateDirectory(target);
        var copied = 0;
        var last = Stopwatch.GetTimestamp();
        foreach (var relative in ArduinoSourceSnapshot.Files(source))
        {
            token.ThrowIfCancellationRequested();
            var destinationPath = PathBoundary.Resolve(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var input = File.OpenRead(PathBoundary.Resolve(source, relative));
            await using var destination = File.Create(destinationPath);
            await input.CopyToAsync(destination, token);
            copied++;
            if (output is not null && Stopwatch.GetElapsedTime(last).TotalMilliseconds >= 500)
            {
                output.Report("准备 Arduino 私有副本… " + copied.ToString("N0") + " 个文件");
                last = Stopwatch.GetTimestamp();
            }
        }
    }

    internal sealed class Execution(ResolvedToolset tools, string root, string data, string libraryRoot,
        ToolUsageLease use, ToolUsageLease cacheLease) : IDisposable
    {
        public ResolvedToolset Tools => tools;
        public string LibraryRoot { get; set; } = libraryRoot;
        public async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, string workingDirectory,
            IProgress<string>? output, CancellationToken token)
        {
            Directory.CreateDirectory(LibraryRoot);
            var downloads = Path.Combine(root, "downloads");
            var buildCache = Path.Combine(root, "build-cache");
            Directory.CreateDirectory(downloads);
            Directory.CreateDirectory(buildCache);
            var esp = Tools.Manifest.ComponentVersions?.ContainsKey("esp32:esp32") == true;
            var config = PathBoundary.Resolve(root, "cli-config.json");
            await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
            {
                directories = new
                {
                    data = esp ? EspressifNativePath.For(data) : data,
                    downloads = esp ? EspressifNativePath.For(downloads) : downloads,
                    user = esp ? EspressifNativePath.For(LibraryRoot) : LibraryRoot
                },
                build_cache = new
                {
                    path = esp ? EspressifNativePath.For(buildCache) : buildCache
                },
                updater = new
                {
                    enable_notification = false
                },
                library = new
                {
                    enable_unsafe_install = false
                }
            }), token);
            var remove = Environment.GetEnvironmentVariables().Keys.Cast<string>()
                .Where(key => key.StartsWith("ARDUINO_", StringComparison.OrdinalIgnoreCase)).ToArray();
            return await new ProcessRunner().RunAsync(new(Tools.Tool("arduino-cli"),
                ["--config-file", config, "--no-color", .. arguments], workingDirectory, TimeSpan.FromMinutes(10),
                RemoveEnvironment: remove, Output: output), token);
        }

        public void Dispose()
        {
            cacheLease.Dispose();
            use.Dispose();
        }
    }
}
