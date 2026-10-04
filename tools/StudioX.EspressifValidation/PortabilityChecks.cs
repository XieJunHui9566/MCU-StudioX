namespace StudioX.EspressifValidation;

using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Application.CodeIntelligence;
using StudioX.Application.Health;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>复制已有的锁定工具至隔离目录，实际移动并重新构建；不下载 SDK，不触碰安装目录或硬件。</summary>
internal static class PortabilityChecks
{
    internal static async Task FinishAsync(string prepared, string evidence)
    {
        if (Directory.Exists(evidence))
        {
            throw new IOException("Use a new finishing evidence directory.");
        }
        Directory.CreateDirectory(evidence);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var token = cancellation.Token;
        var checks = new List<string>();
        var stages = new List<object>();
        var passed = false;
        void Check(bool value, string text)
        {
            if (!value)
            {
                throw new InvalidOperationException(text);
            }
            checks.Add(text);
            Console.WriteLine("PASS " + text);
        }
        try
        {
            var runtime = Path.Combine(prepared, "installation moved/runtime");
            var builds = new BuildService(new ToolsetCatalog(Path.Combine(runtime, "toolsets")));
            var health = new ProjectHealthService(new ToolsetCatalog(Path.Combine(runtime, "toolsets")), builds);
            foreach (var name in new[] { "portable_esp", "portable_arm" })
            {
                var root = Path.Combine(prepared, "projects moved", name);
                var project = await ProjectService.ReadAsync(root, token);
                if (project.Name != name || name == "portable_esp" && project.Espressif?.Target != "esp32c3" || name == "portable_arm" && project.DeviceId != "STM32F407ZG")
                {
                    throw new IOException("Prepared moved fixture has unexpected identity.");
                }
                var retained = await InputsAsync(root, token);
                var backupCaches = Directory.EnumerateFiles(Path.Combine(root, ".build"), "CMakeCache.txt", SearchOption.AllDirectories)
                    .Where(path => path.Contains(".studiox-cache-backups", StringComparison.Ordinal)).ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
                var plan = await health.PreviewCacheRepairAsync(root, token);
                if (name == "portable_esp")
                {
                    Check(plan.Entries.Any(entry => entry.RelativePath == ".build/toolchain") && plan.Entries.Any(entry => entry.RelativePath == ".build/specs"), "SDK cache preview includes response files and specs with old absolute paths");
                }
                else
                {
                    await health.RepairCacheAsync(plan, token);
                    Check(true, "moved ARM configuration cache is backed up through the existing repair service");
                }
                var report = await builds.BuildAsync(root, cancellationToken: token);
                await File.WriteAllTextAsync(Path.Combine(evidence, name + "-after.log"), report.Log, token);
                stages.Add(new
                {
                    root,
                    report.Success,
                    report.ExitCode,
                    report.Artifacts
                });
                Check(report.Success, name + " compiles after moving the installation and project into paths with spaces");
                Check(backupCaches.All(pair => File.Exists(pair.Key) && pair.Value == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Key)))), "native reconfiguration preserves previous cache-backup evidence");
                var after = await InputsAsync(root, token);
                Check(retained.All(pair => after.GetValueOrDefault(pair.Key) == pair.Value), name + " relocation keeps source, sdkconfig and tool locks unchanged");
                if (name == "portable_esp")
                {
                    Check(report.Log.Contains("原生参数缓存已备份"), "changed build environment automatically backs up stale native parameter caches");
                    await EnvironmentReloadChecks.RunAsync(runtime, root, evidence, Check, token);
                }
            }
            passed = true;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
            {
                passed,
                checks,
                stages,
                hardware = false,
                downloadedSdk = false,
                cleanWindowsVmTested = false,
                applicationAssemblySha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(CodeIntelligenceService).Assembly.Location)))
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    internal static async Task RunAsync(string sourceRuntime, string espArchive, string armArchive, string output, bool usePreparedFixture = false)
    {
        if (Directory.Exists(output) && !usePreparedFixture)
        {
            throw new IOException("Use a new portability evidence directory.");
        }
        if (usePreparedFixture)
        {
            var previous = Path.Combine(output, "result.json");
            if (!File.Exists(previous) || !Directory.Exists(Path.Combine(output, "installation-before/runtime")))
            {
                throw new IOException("Prepared portability fixture is unavailable.");
            }
            File.Copy(previous, Path.Combine(output, "result-previous-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ".json"), false);
        }
        Directory.CreateDirectory(output);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var token = cancellation.Token;
        var checks = new List<string>();
        var stages = new List<object>();
        var passed = false;
        void Check(bool value, string text)
        {
            if (!value)
            {
                throw new InvalidOperationException(text);
            }
            checks.Add(text);
            Console.WriteLine("PASS " + text);
        }
        var variables = new[] { "PATH", "IDF_PATH", "IDF_TOOLS_PATH", "IDF_PYTHON_ENV_PATH", "PYTHONHOME", "PYTHONPATH" };
        var ambient = variables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            var repository = new PackRepository(Path.Combine(output, "packs"));
            var espPack = await repository.ImportAsync(espArchive, token);
            var armPack = await repository.ImportAsync(armArchive, token);
            var espDevice = espPack.Manifest.Devices.Single(device => device.Espressif?.Target == "esp32c3");
            var armDevice = armPack.Manifest.Devices.Single(device => device.Id == "STM32F407ZG");
            var armTemplate = armDevice.Templates.First(template => template.Id.Contains("hal", StringComparison.OrdinalIgnoreCase) && !template.Id.Contains("rtos", StringComparison.OrdinalIgnoreCase));
            var runtime = Path.Combine(output, "installation-before/runtime");
            Directory.CreateDirectory(runtime);
            var projects = Path.Combine(output, "projects-before");
            var espRoot = Path.Combine(projects, "portable_esp");
            var armRoot = Path.Combine(projects, "portable_arm");
            if (!usePreparedFixture)
            {
                await new ProjectService().CreateAsync(espPack, espDevice.Id, "hello-world", "portable_esp", espRoot, token);
                await new ProjectService().CreateAsync(armPack, armDevice.Id, armTemplate.Id, "portable_arm", armRoot, token);
            }
            else
            {
                var esp = await ProjectService.ReadAsync(espRoot, token);
                var arm = await ProjectService.ReadAsync(armRoot, token);
                if (esp.PackContentHash != espPack.ContentHash || arm.PackContentHash != armPack.ContentHash || esp.DeviceId != espDevice.Id || arm.DeviceId != armDevice.Id)
                {
                    throw new IOException("Prepared fixture identity differs from the selected packs.");
                }
            }
            var catalog = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
            var health = new ProjectHealthService(catalog, new BuildService(catalog));
            if (!usePreparedFixture)
            {
                Check((await health.InspectAsync(espRoot, token: token)).Checks.Any(item => item.Code == "TOOLSET_MISSING" && item.ToolsetVersion == espDevice.ToolsetVersion), "empty installation identifies the precise SDK version to select");
                foreach (var device in new[] { espDevice, armDevice })
                {
                    var relative = $"toolsets/{device.ToolsetId}/{device.ToolsetVersion}";
                    Console.WriteLine("COPY " + relative);
                    await CopyAsync(Path.Combine(sourceRuntime, relative), Path.Combine(runtime, relative), token);
                }
                await CopyAsync(Path.Combine(sourceRuntime, "languages/clangd"), Path.Combine(runtime, "languages/clangd"), token);
            }
            Check(Directory.GetDirectories(Path.Combine(runtime, "toolsets")).Length == 2, "isolated installation contains only the two selected development environments");
            // 仅改变验证进程的环境；子构建必须使用锁定工具，系统与用户级 PATH 完全不动。
            Environment.SetEnvironmentVariable("PATH", Environment.GetFolderPath(Environment.SpecialFolder.System));
            foreach (var name in variables.Where(name => name != "PATH"))
            {
                Environment.SetEnvironmentVariable(name, Path.Combine(output, "invalid-ambient", name));
            }
            var builds = new BuildService(catalog);
            foreach (var root in new[] { espRoot, armRoot })
            {
                await BuildAsync(builds, root, "before");
            }
            var reloadEvidence = Path.Combine(output, "reload-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Directory.CreateDirectory(reloadEvidence);
            await EnvironmentReloadChecks.RunAsync(runtime, espRoot, reloadEvidence, Check, token);
            var retained = new Dictionary<string, Dictionary<string, string>>();
            foreach (var root in new[] { espRoot, armRoot })
            {
                retained[Path.GetFileName(root)] = await InputsAsync(root, token);
            }
            var movedRuntime = Path.Combine(output, "installation moved/runtime");
            var movedProjects = Path.Combine(output, "projects moved");
            Directory.CreateDirectory(Path.GetDirectoryName(movedRuntime)!);
            RequireWithinOutput(runtime);
            RequireWithinOutput(movedRuntime);
            RequireWithinOutput(projects);
            RequireWithinOutput(movedProjects);
            Directory.Move(runtime, movedRuntime);
            Directory.Move(projects, movedProjects);
            var movedCatalog = new ToolsetCatalog(Path.Combine(movedRuntime, "toolsets"));
            var movedBuilds = new BuildService(movedCatalog);
            var movedHealth = new ProjectHealthService(movedCatalog, movedBuilds);
            foreach (var name in new[] { "portable_esp", "portable_arm" })
            {
                var root = Path.Combine(movedProjects, name);
                var report = await movedHealth.InspectAsync(root, token: token);
                await movedHealth.ExportAsync(report, Path.Combine(output, name + "-moved-health.json"), token);
                Check(report.Checks.Any(item => item.Code == "HEALTH_CACHE_PROJECT" && item.Action == HealthAction.ResetCache), name + " movement is explained before reusing the old CMake cache");
                if (name == "portable_esp")
                {
                    await using var language = new CodeIntelligenceService(movedRuntime, Path.Combine(output, "moved-language"));
                    try
                    {
                        await language.StartAsync(root, token);
                        throw new InvalidOperationException("Expected stale configuration rejection.");
                    }
                    catch (StudioXException error) when (error.Code == "LANGUAGE_CACHE_PROJECT") { Check(true, "moved ESP analysis refuses the old absolute database paths"); }
                }
                var plan = await movedHealth.PreviewCacheRepairAsync(root, token);
                Check(plan.Entries.Count > 0, name + " repair has a reviewable generated-cache plan");
                var backup = await movedHealth.RepairCacheAsync(plan, token);
                Check(Directory.Exists(backup), name + " repair preserves the old cache in a backup");
                var afterRepair = await InputsAsync(root, token);
                Check(retained[name].All(pair => afterRepair.GetValueOrDefault(pair.Key) == pair.Value), name + " cache repair preserves sources, sdkconfig and content locks");
                await BuildAsync(movedBuilds, root, "after");
                var afterBuild = await InputsAsync(root, token);
                Check(retained[name].Where(pair => pair.Key.StartsWith(".studiox/", StringComparison.Ordinal)).All(pair => afterBuild.GetValueOrDefault(pair.Key) == pair.Value), name + " rebuilt content locks are unchanged after moving tools and project");
                if (name == "portable_esp")
                {
                    await using var language = new CodeIntelligenceService(movedRuntime, Path.Combine(output, "moved-language-ready"));
                    await language.StartAsync(root, token);
                    Check(language.IsReady, "ESP language analysis starts with the moved SDK and newly generated database");
                }
            }
            passed = true;
            async Task BuildAsync(BuildService service, string root, string stage)
            {
                Console.WriteLine("BUILD " + stage + " " + Path.GetFileName(root));
                var report = await service.BuildAsync(root, cancellationToken: token);
                await File.WriteAllTextAsync(Path.Combine(output, Path.GetFileName(root) + "-" + stage + ".log"), report.Log, token);
                stages.Add(new
                {
                    project = root,
                    stage,
                    report.Success,
                    report.ExitCode,
                    report.LogPath,
                    report.Artifacts
                });
                Check(report.Success, Path.GetFileName(root) + " " + stage + " actually compiles with isolated managed tools and poisoned ambient variables");
            }
            void RequireWithinOutput(string path)
            {
                if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(output) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Move outside evidence directory rejected.");
                }
            }
        }
        finally
        {
            foreach (var (name, value) in ambient)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            {
                passed,
                checks,
                stages,
                usedPreparedFixture = usePreparedFixture,
                hardware = false,
                downloadedSdk = false,
                cleanWindowsVmTested = false,
                applicationAssemblySha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(CodeIntelligenceService).Assembly.Location))),
                processEnvironmentRestored = variables.All(name => Environment.GetEnvironmentVariable(name) == ambient[name])
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    private static async Task CopyAsync(string source, string target, CancellationToken token)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Linked runtime file requires explicit handling: " + file);
            }
            var destination = PathBoundary.Resolve(target, Path.GetRelativePath(source, file).Replace('\\', '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
            await input.CopyToAsync(output, token);
        }
    }
    private static async Task<Dictionary<string, string>> InputsAsync(string root, CancellationToken token)
    {
        var result = new Dictionary<string, string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith(".build/", StringComparison.Ordinal))
            {
                continue;
            }
            await using var stream = File.OpenRead(file);
            result[relative] = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        }
        return result;
    }
}
