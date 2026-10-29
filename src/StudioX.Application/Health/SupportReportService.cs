namespace StudioX.Application.Health;

using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using StudioX.Engine;
using StudioX.Foundation;

/// <summary>本机故障资料；只读取明确配置和有界日志，不收集源码、凭据、完整环境变量或硬件状态。</summary>
public sealed class SupportReportService(string runtimeDirectory, string dataDirectory, ProjectHealthService health)
{
    private const int TextLimit = 256 * 1024;
    public async Task ExportAsync(string? directory, string path, string diagnostic = "", CancellationToken token = default)
    {
        var report = await health.InspectAsync(directory, token: token).ConfigureAwait(false);
        var files = new List<SupportReportFile>();
        ProjectManifest? project = null;
        var errors = new List<string>();
        if (directory is not null)
        {
            var root = Path.GetFullPath(directory);
            try
            {
                project = await ProjectService.ReadAsync(root, token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException) { errors.Add(error.ToString()); }
            foreach (var relative in new[] { ".studiox/project.json", ".studiox/toolchain.lock.json", DevelopmentComponentLock.RelativePath,
                ProjectBuildSettings.RelativePath, ".studiox/arduino.lock.json", ".studiox/arduino-build.json", ".studiox/pic-build.json",
                ".build/studiox-build-receipt.json", ".studiox/zephyr-device.json", "CMakePresets.json", "prj.conf" }.Distinct())
            {
                files.Add(await ReadFileAsync(root, relative, token).ConfigureAwait(false));
            }
            try
            {
                // 仅按已知日志文件名遍历三层目录；不进入对象、第三方库或 SDK 树。
                var candidates = new List<string>();
                var pending = new Queue<(string Relative, int Depth)>();
                pending.Enqueue((".build", 0));
                var visited = 0;
                while (pending.TryDequeue(out var item) && visited++ < 256)
                {
                    token.ThrowIfCancellationRequested();
                    var current = PathBoundary.Resolve(root, item.Relative);
                    if (!Directory.Exists(current))
                    {
                        continue;
                    }
                    foreach (var name in new[] { "build.log", "studiox-build.log", "CMakeConfigureLog.yaml", "CMakeError.log" })
                    {
                        var relative = item.Relative + "/" + name;
                        if (File.Exists(PathBoundary.Resolve(root, relative)))
                        {
                            candidates.Add(relative);
                        }
                    }
                    if (item.Depth < 2)
                    {
                        foreach (var child in Directory.EnumerateDirectories(current).Take(128))
                        {
                            pending.Enqueue((item.Relative + "/" + Path.GetFileName(child), item.Depth + 1));
                        }
                    }
                }
                foreach (var relative in candidates.OrderByDescending(relative => File.GetLastWriteTimeUtc(PathBoundary.Resolve(root, relative))).Take(6))
                {
                    files.Add(await ReadFileAsync(root, relative, token).ConfigureAwait(false));
                }
                if (pending.Count > 0)
                {
                    errors.Add("日志目录扫描达到 256 项上限；仅包含已发现的最近六份日志。");
                }
            }
            catch (Exception error) when (error is not OperationCanceledException) { errors.Add(error.ToString()); }
        }
        var tools = new List<object>();
        try
        {
            var root = Path.GetFullPath(Path.Combine(runtimeDirectory, "toolsets"));
            if (Directory.Exists(root))
            {
                foreach (var id in Directory.EnumerateDirectories(root).Where(item => !Path.GetFileName(item).StartsWith('.')).Take(128))
                {
                    _ = PathBoundary.Resolve(root, Path.GetFileName(id));
                    foreach (var version in Directory.EnumerateDirectories(id).Take(16))
                    {
                        var relative = Path.GetFileName(id) + "/" + Path.GetFileName(version) + "/toolset.json";
                        try
                        {
                            var manifestPath = PathBoundary.Resolve(root, relative);
                            if (new FileInfo(manifestPath).Length > 32 * 1024 * 1024)
                            {
                                throw new IOException("工具清单超过 32 MiB。");
                            }
                            var manifest = await JsonStore.ReadAsync<ToolsetManifest>(manifestPath, token).ConfigureAwait(false);
                            await using var stream = File.OpenRead(manifestPath);
                            tools.Add(new
                            {
                                manifest.Id,
                                manifest.Version,
                                manifest.CompilerId,
                                manifest.Host,
                                manifest.Purpose,
                                manifest.ComponentVersions,
                                roles = manifest.Executables.Keys,
                                indexedFiles = manifest.Sha256.Count,
                                manifestSha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false))
                            });
                        }
                        catch (Exception error) when (error is not OperationCanceledException) { errors.Add(relative + ": " + error); }
                    }
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException) { errors.Add(error.ToString()); }
        var assembly = typeof(SupportReportService).Assembly;
        var disks = new List<object>();
        foreach (var location in new[] { directory, runtimeDirectory, dataDirectory }.Where(location => location is not null)
            .Select(location => Path.GetPathRoot(Path.GetFullPath(location!))!).Distinct(HostPlatform.PathComparer))
        {
            try
            {
                var drive = new DriveInfo(location);
                disks.Add(new
                {
                    drive.Name,
                    drive.TotalSize,
                    drive.AvailableFreeSpace
                });
            }
            catch (Exception error) when (error is not OperationCanceledException) { errors.Add(error.ToString()); }
        }
        await JsonStore.WriteAsync(path, new
        {
            formatVersion = 1,
            collectedUtc = DateTimeOffset.UtcNow,
            productVersion = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(attribute => attribute.Key == "StudioXProductVersion")?.Value,
            assemblyVersion = assembly.GetName().Version?.ToString(),
            environment = new
            {
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                runtime = RuntimeInformation.FrameworkDescription,
                runtimeDirectory,
                dataDirectory,
                ambientVariableNames = ToolsetEnvironment.AmbientVariables.Where(name => Environment.GetEnvironmentVariable(name) is not null).ToArray()
            },
            project,
            capabilities = ProjectCapabilities.For(project),
            health = report,
            tools,
            disks,
            files,
            diagnostic = diagnostic.Length > TextLimit ? diagnostic[^TextLimit..] : diagnostic,
            diagnosticTruncated = diagnostic.Length > TextLimit,
            errors,
            scope = "本机只读快照；快速检查未完整校验工具内容。日志最多六份，每份保留末尾 256 KiB；未收集源码、AI/插件设置、凭据或环境变量值，未连接硬件。"
        }, token).ConfigureAwait(false);
    }

    private static async Task<SupportReportFile> ReadFileAsync(string root, string relative, CancellationToken token)
    {
        try
        {
            var path = PathBoundary.Resolve(root, relative);
            if (!File.Exists(path))
            {
                return new(relative, null, null, null, false, "文件不存在");
            }
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
            var length = stream.Length;
            var truncated = length > TextLimit;
            if (truncated)
            {
                stream.Seek(-TextLimit, SeekOrigin.End);
            }
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
            var buffer = new char[TextLimit];
            var count = await reader.ReadBlockAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            return new(relative, length, File.GetLastWriteTimeUtc(path), new string(buffer, 0, count), truncated, null);
        }
        catch (Exception error) when (error is not OperationCanceledException) { return new(relative, null, null, null, false, error.ToString()); }
    }
}
