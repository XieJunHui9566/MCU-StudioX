namespace StudioX.EnvironmentReliabilityValidation;

using System.Diagnostics;
using System.Security.Cryptography;
using StudioX.Application;
using StudioX.Application.CodeIntelligence;
using StudioX.Application.Tools;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class OfflineChecks
{
    internal sealed record Plan(int FormatVersion, string[] Archives, string LanguagesDirectory, Case[] Cases, Upgrade[]? Upgrades = null);
    internal sealed record Case(string Name, string Pack, string Device, string Template);
    internal sealed record Upgrade(string SourceCase, string TargetCase);
    internal static async Task RunAsync(string planPath, string output, bool resume = false)
    {
        if (Directory.Exists(output) != resume)
        {
            throw new IOException("Use a new offline evidence directory, or explicitly resume an existing fixture.");
        }
        var plan = await JsonStore.ReadAsync<Plan>(planPath);
        if (plan.FormatVersion != 1 || plan.Archives.Length is < 1 or > 16 || plan.Cases.Length is < 1 or > 16)
        {
            throw new ArgumentException("Explicit offline inputs required.");
        }
        var inputRoot = Path.GetDirectoryName(planPath)!;
        string Input(string path) => Path.IsPathRooted(path) ? Path.GetFullPath(path) : PathBoundary.Resolve(inputRoot, path);
        Directory.CreateDirectory(output);
        var planBytes = await File.ReadAllBytesAsync(planPath);
        var planHash = Convert.ToHexString(SHA256.HashData(planBytes));
        var savedPlan = Path.Combine(output, "input-plan.json");
        if (File.Exists(savedPlan) && !File.ReadAllBytes(savedPlan).SequenceEqual(planBytes))
        {
            throw new IOException("The resumed fixture requires its original explicit plan.");
        }
        if (resume)
        {
            var previousRun = Path.Combine(output, "attempts", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(previousRun);
            foreach (var path in Directory.EnumerateFiles(output).Where(path => Path.GetExtension(path) is ".log" or ".json"))
            {
                File.Copy(path, Path.Combine(previousRun, Path.GetFileName(path)), overwrite: false);
            }
        }
        if (!File.Exists(savedPlan))
        {
            File.Copy(planPath, savedPlan, overwrite: false);
        }
        // 干净 Windows 首次导入两版完整 IDF 的全量文件校验耗时较长；给完整校验和实编保留有界时间。
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(90));
        var token = timeout.Token;
        var checks = new List<string>();
        var stages = new List<object>();
        var passed = false;
        string? diagnostic = null;
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
        var previous = variables.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var runtime = Path.Combine(output, "selected installation/runtime");
        var tools = new ToolsetCatalog(Path.Combine(runtime, "toolsets"), Path.Combine(output, "user-data"));
        var packs = new PackRepository(Path.Combine(output, "packs"));
        var projects = new Dictionary<string, (string Root, InstalledPack Pack)>();
        var manager = new ToolManagementService(tools, packs, new(output), Path.Combine(output, "user-data"));
        var environment = new ToolEnvironmentService(tools);
        var preparation = new ProjectToolPreparationService(tools, manager);
        var builds = new BuildService(tools);
        try
        {
            foreach (var item in plan.Cases)
            {
                PackValidator.Token(item.Name);
                var pack = await packs.ImportAsync(Input(item.Pack), token);
                var root = Path.Combine(output, "projects", item.Name);
                if (resume)
                {
                    var existing = await ProjectService.ReadAsync(root, token);
                    Check(existing.Name == item.Name && existing.PackId == pack.Manifest.Id && existing.PackVersion == pack.Manifest.Version
                        && existing.PackContentHash == pack.ContentHash && existing.DeviceId == item.Device && existing.TemplateId == item.Template,
                        item.Name + " resume preserves the exact existing project and pack identity");
                }
                else
                {
                    await new ProjectService().CreateAsync(pack, item.Device, item.Template, item.Name, root, token);
                }
                projects.Add(item.Name, (root, pack));
                var required = await preparation.InspectAsync(root, token: token);
                if (!resume)
                {
                    Check(required.Requirements.All(need => need.State == ProjectToolState.Missing), item.Name + " explains exact missing components in an empty installation");
                }
            }
            foreach (var archivePath in plan.Archives)
            {
                var preview = await manager.PreviewInstallAsync(Input(archivePath), token: token);
                var result = await manager.InstallAsync(preview, new ConsoleProgress(), token);
                Check(resume || !result.AlreadyInstalled, "offline import fully verifies " + preview.Id + "/" + preview.Version);
                Check((await manager.InstallAsync(preview, token: token)).AlreadyInstalled, "duplicate offline import verifies and preserves " + preview.Id + "/" + preview.Version);
            }
            Check(tools.ManifestPaths().Count() == plan.Archives.Length, "installation contains only explicitly selected component versions");
            var languageSource = Input(plan.LanguagesDirectory);
            var languageTarget = Path.Combine(runtime, "languages");
            Directory.CreateDirectory(languageTarget);
            foreach (var file in Directory.EnumerateFiles(languageSource, "*", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                var target = PathBoundary.Resolve(languageTarget, Path.GetRelativePath(languageSource, file).Replace('\\', '/'));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (resume && File.Exists(target))
                {
                    using var original = File.OpenRead(file);
                    using var existing = File.OpenRead(target);
                    if (!SHA256.HashData(original).SequenceEqual(SHA256.HashData(existing)))
                    {
                        throw new IOException("Existing language runtime differs: " + target);
                    }
                }
                else
                {
                    File.Copy(file, target, overwrite: false);
                }
            }
            Environment.SetEnvironmentVariable("PATH", Environment.GetFolderPath(Environment.SpecialFolder.System));
            foreach (var variable in variables.Skip(1))
            {
                Environment.SetEnvironmentVariable(variable, Path.Combine(output, "invalid-ambient", variable));
            }
            foreach (var (name, entry) in projects)
            {
                Check((await preparation.InspectAsync(entry.Root, token: token)).Requirements.All(need => need.State == ProjectToolState.Installed), name + " resolves exact versions without global developer tools");
                var report = await builds.BuildAsync(entry.Root, cancellationToken: token);
                await File.WriteAllTextAsync(Path.Combine(output, name + "-build.log"), report.Log, token);
                stages.Add(new
                {
                    name,
                    report.Success,
                    report.ExitCode,
                    report.Artifacts
                });
                Check(report.Success, name + " actually compiles using only offline selected components");
                await using var language = new CodeIntelligenceService(runtime, Path.Combine(output, "language-data", name));
                await language.StartAsync(entry.Root, token);
                Check(language.IsReady, name + " starts clangd with the same selected SDK and generated configuration");
                var manifest = await ProjectService.ReadAsync(entry.Root, token);
                var entryFile = manifest.EntryFile ?? "src/main.c";
                var source = await File.ReadAllTextAsync(PathBoundary.Resolve(entry.Root, entryFile), token);
                await language.SynchronizeDiagnosticsAsync(entryFile, source, [new(entryFile, source)], token);
                var ready = false;
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    if (language.GetDiagnostics().Any(batch => batch.Path == entryFile))
                    {
                        ready = true;
                        break;
                    }
                    await Task.Delay(100, token);
                }
                Check(ready && !language.GetDiagnostics().SelectMany(batch => batch.Items).Any(item => item.Severity == 1), name + " editor reports no errors for firmware proven to compile");
            }
            foreach (var upgrade in plan.Upgrades ?? [])
            {
                var source = projects[upgrade.SourceCase];
                var target = projects[upgrade.TargetCase];
                var before = await SourceHashesAsync(source.Root, token);
                var service = new ComponentMigrationService(packs, builds);
                var upgradeName = "upgrade-" + upgrade.TargetCase + (resume ? "-" + Guid.NewGuid().ToString("N") : "");
                var preview = await service.PreviewAsync(source.Root, target.Pack, Path.Combine(output, upgradeName), token);
                Check(preview.CanCreate, "SDK upgrade has a reviewable isolated-copy plan: " + string.Join(';', preview.Blockers));
                var result = await service.CreateAndBuildAsync(preview, token: token);
                Check(result.Success, "explicit SDK upgrade copy actually compiles");
                var after = await SourceHashesAsync(source.Root, token);
                Check(before.Count == after.Count && before.All(pair => after.GetValueOrDefault(pair.Key) == pair.Value), "SDK upgrade preserves original source, sdkconfig and locked version bytes");
            }
            passed = true;
        }
        catch (Exception error) { diagnostic = error.ToString(); throw; }
        finally
        {
            foreach (var (key, value) in previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
            await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
            {
                passed,
                checks,
                stages,
                diagnostic,
                resumed = resume,
                inputPlanSha256 = planHash,
                hardware = false,
                downloadedSdk = false,
                cleanWindowsVmTested = false,
                processEnvironmentRestored = variables.All(key => Environment.GetEnvironmentVariable(key) == previous[key]),
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                machine = Environment.MachineName,
                applicationSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(ToolEnvironmentService).Assembly.Location)))
            });
        }
    }
    private static async Task<Dictionary<string, string>> SourceHashesAsync(string root, CancellationToken token)
    {
        var result = new Dictionary<string, string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith(".build/", StringComparison.Ordinal))
            {
                continue;
            }
            result[relative] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file, token)));
        }
        return result;
    }
    private sealed class ConsoleProgress : IProgress<string>
    {
        private long last;
        public void Report(string value)
        {
            var now = Stopwatch.GetTimestamp();
            if (last != 0 && Stopwatch.GetElapsedTime(last, now).TotalSeconds < 5)
            {
                return;
            }
            last = now;
            Console.WriteLine(value);
        }
    }
}
