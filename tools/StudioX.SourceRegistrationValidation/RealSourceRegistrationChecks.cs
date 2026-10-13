using System.Text.Json;
using StudioX.Application;
using StudioX.Application.BuildConfiguration;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>明确输入已有包与工具；真实编译只发生在全新的验收工程。</summary>
internal static class RealSourceRegistrationChecks
{
    private sealed record Input(string Runtime, string Packs, string NativePackId, string NativePackVersion, string NativeDevice, string NativeTemplate, string IdfPackId, string IdfPackVersion, string IdfDevice, string IdfTemplate);
    public static async Task RunAsync(string output, string inputFile, SourceRegistrationService service, ProjectFileService files, Action<bool, string> check)
    {
        var input = await JsonStore.ReadAsync<Input>(inputFile);
        Directory.CreateDirectory(output);
        var catalog = await new PackRepository(input.Packs).ListCatalogAsync();
        var builder = new BuildService(new ToolsetCatalog(Path.Combine(input.Runtime, "toolsets")));
        var reports = new List<object>();
        foreach (var idf in new[] { false, true })
        {
            var selected = catalog.Single(pack => pack.Manifest.Id == (idf ? input.IdfPackId : input.NativePackId) && pack.Manifest.Version == (idf ? input.IdfPackVersion : input.NativePackVersion));
            var pack = await PackRepository.VerifyAsync(selected);
            var directory = Path.Combine(output, idf ? "idf" : "native");
            var manifest = await new ProjectService().CreateAsync(pack, idf ? input.IdfDevice : input.NativeDevice, idf ? input.IdfTemplate : input.NativeTemplate, "registration_check", directory);
            var configuration = idf ? "main/CMakeLists.txt" : "CMakeLists.txt";
            var original = await File.ReadAllTextAsync(PathBoundary.Resolve(directory, configuration));
            var added = idf ? "main/registration_added.c" : "src/registration_added.c";
            var renamed = idf ? "main/registration_renamed.c" : "src/registration_renamed.c";
            await File.WriteAllTextAsync(PathBoundary.Resolve(directory, added), "int studiox_registration_evidence(void) { return 7; }\n");
            foreach (var step in new[] { "add", "rename", "remove" })
            {
                if (step == "rename")
                {
                    File.Move(PathBoundary.Resolve(directory, added), PathBoundary.Resolve(directory, renamed));
                }
                var context = await service.ReadAsync(directory, configuration, []);
                var operation = step switch
                {
                    "add" => new SourceRegistrationOperation(SourceRegistrationKind.Add, added),
                    "rename" => new(SourceRegistrationKind.Rename, added, renamed),
                    _ => new(SourceRegistrationKind.Remove, renamed)
                };
                var plan = service.Prepare(context, context.Targets.Single().Name, [operation]);
                await service.ValidateAsync(plan, []);
                await files.SaveAsync(directory, plan.Change.Source, plan.Change.After);
                Console.WriteLine("BUILD " + (idf ? "ESP-IDF" : "native MCU") + " " + step);
                var report = await builder.BuildAsync(directory);
                await File.WriteAllTextAsync(Path.Combine(output, (idf ? "idf-" : "native-") + step + ".log"), report.Log);
                check(report.Success, (idf ? "ESP-IDF" : "native MCU") + " " + step + " registration configures, compiles and links");
                var database = PathBoundary.Resolve(directory, ".build/compile_commands.json");
                check(File.Exists(database), "current application compile database exists for " + (idf ? "IDF " : "native ") + step);
                // IDF 引导程序有独立数据库；当前应用的登记只核对应用构建目录。
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(database));
                var entries = json.RootElement.EnumerateArray().Select(item => Path.GetFullPath(item.GetProperty("file").GetString()!, item.GetProperty("directory").GetString()!)).ToArray();
                var expected = step == "add" ? added : renamed;
                check(entries.Contains(PathBoundary.Resolve(directory, expected), StringComparer.OrdinalIgnoreCase) == (step != "remove") &&
                    (step == "add" || !entries.Contains(PathBoundary.Resolve(directory, added), StringComparer.OrdinalIgnoreCase)), "compile database matches " + (idf ? "IDF " : "native ") + step + " with no stale old source");
                if (step == "remove")
                {
                    check(File.Exists(PathBoundary.Resolve(directory, renamed)), "unregistering retains physical source for " + (idf ? "IDF" : "native"));
                }
                reports.Add(new
                {
                    kind = idf ? "esp-idf" : "native",
                    step,
                    report.Success,
                    report.ExitCode,
                    manifest.DeviceId,
                    manifest.ToolsetId,
                    manifest.ToolsetVersion,
                    manifest.CompilerId,
                    pack.Manifest.Id,
                    pack.Manifest.Version,
                    pack.ContentHash,
                    hardware = false
                });
            }
            var after = await File.ReadAllTextAsync(PathBoundary.Resolve(directory, configuration));
            check(original.Split('\n').Where(line => line.TrimStart().StartsWith('#')).All(line => after.Contains(line, StringComparison.Ordinal)), "real template comments survive all registration steps");
        }
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            success = true,
            reports,
            hardware = false
        });
    }
}
