using StudioX.Application.CodeIntelligence;
using StudioX.Application.Editing;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>真实 clangd 验证未打开头文件变化与翻译单元增删；只使用隔离软件夹具。</summary>
internal static class LanguageFileChangeChecks
{
    public static async Task RunAsync(string runtime, string output, Action<bool, string> check)
    {
        var project = Path.Combine(output, "project");
        Directory.CreateDirectory(Path.Combine(project, ".studiox"));
        Directory.CreateDirectory(Path.Combine(project, "device"));
        Directory.CreateDirectory(Path.Combine(project, "src"));
        const string main = "#include \"shared.hh\"\nint main(void) { return shared(1); }\n";
        const string header = "#ifdef STUDIOX_SYNC_CONFIG\n#error changed language configuration\n#endif\nstatic inline int shared(int value) { return value; }\n";
        await File.WriteAllTextAsync(Path.Combine(project, "src/main.c"), main);
        await File.WriteAllTextAsync(Path.Combine(project, "src/shared.hh"), header);
        await JsonStore.WriteAsync(Path.Combine(project, ".studiox/project.json"), new ProjectManifest(1, "file-sync-language", "offline.pack", "1.0.0",
            "offline", "offline-device", "plain", "offline.tools", "1.0.0", "gcc"));
        var device = new DeviceDefinition("offline-device", "Offline", "arm", 0x08000000, 65536, 0x20000000, 16384,
            "offline.tools", "1.0.0", "gcc", [], ["src"], ["src/main.c"], [], "linker.ld", [], [],
            [new ProjectTemplate("plain", "Plain", "Offline language fixture", "src/main.c")]);
        await JsonStore.WriteAsync(Path.Combine(project, "device/manifest.json"), new PackManifest(1, "offline.pack", "1.0.0", "Offline", "Offline", [device]));
        await using var service = new CodeIntelligenceService(runtime, Path.Combine(output, "user-data"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        async Task<CodeDiagnosticBatch> Analyze(string path, string text)
        {
            await service.SynchronizeDiagnosticsAsync(path, text, [new(path, text)], token);
            for (var i = 0; i < 120; i++)
            {
                if (service.GetDiagnostics().SingleOrDefault(batch => batch.Path == path && batch.Text == text) is { } batch)
                {
                    return batch;
                }
                await Task.Delay(25, token);
            }
            throw new InvalidOperationException("Fresh disk-dependent diagnostics missing.\n" + string.Join('\n', service.DrainLog()));
        }
        await service.StartAsync(project, token);
        check((await Analyze("src/main.c", main)).Items.All(item => item.Severity != 1), "real clangd baseline resolves a disk header without opening it");
        await File.WriteAllTextAsync(Path.Combine(project, "src/shared.hh"), "static inline int shared(int first, int second) { return first + second; }\n", token);
        await service.RefreshFilesAsync(new(project, 1, [new("src/shared.hh")]), token);
        check(service.GetDiagnostics().Count == 0, "disk change immediately withdraws the previous language snapshot");
        check((await Analyze("src/main.c", main)).Items.Any(item => item.Severity == 1), "unopened changed header reparses its open source consumer");
        await File.WriteAllTextAsync(Path.Combine(project, "src/shared.hh"), header, token);
        await service.RefreshFilesAsync(new(project, 2, [new("src/shared.hh")]), token);
        check((await Analyze("src/main.c", main)).Items.All(item => item.Severity != 1), "restored disk header clears obsolete errors");
        const string added = "int auxiliary(void) { return 42; }\n";
        await File.WriteAllTextAsync(Path.Combine(project, "src/new.c"), added, token);
        await service.RefreshFilesAsync(new(project, 3, [new("src/new.c", ProjectFileChangeKind.Created)]), token);
        check((await Analyze("src/new.c", added)).Items.All(item => item.Severity != 1), "new translation unit is included in fallback analysis discovery");
        Directory.CreateDirectory(Path.Combine(project, ".build"));
        var database = Path.Combine(project, ".build/compile_commands.json");
        async Task WriteDatabase(bool define) => await JsonStore.WriteAsync(database, new[] { new
        {
            directory = project, file = Path.Combine(project, "src/main.c"),
            arguments = new[] { "arm-none-eabi-gcc", "-std=c17", "-Isrc" }.Concat(define ? ["-DSTUDIOX_SYNC_CONFIG"] : Array.Empty<string>()).Concat(["-c", "src/main.c"]).ToArray()
        } }, token);
        await WriteDatabase(true);
        await service.RefreshFilesAsync(new(project, 4, [new(".build/compile_commands.json", ProjectFileChangeKind.Created)]), token);
        check((await Analyze("src/main.c", main)).Items.Any(item => item.Severity == 1 && item.Message.Contains("changed language configuration", StringComparison.Ordinal)),
            "new compile database changes actual source diagnostics");
        await WriteDatabase(false);
        await service.RefreshFilesAsync(new(project, 5, [new(".build/compile_commands.json")]), token);
        check((await Analyze("src/main.c", main)).Items.All(item => item.Severity != 1), "edited compile database restores clean diagnostics without editing source");
        File.Delete(Path.Combine(project, "src/shared.hh"));
        await service.RefreshFilesAsync(new(project, 6, [new("src/shared.hh", ProjectFileChangeKind.Deleted)]), token);
        check((await Analyze("src/main.c", main)).Items.Any(item => item.Severity == 1), "removed unopened header cannot retain clean consumer diagnostics");
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            success = true,
            hardware = false,
            log = service.DrainLog()
        }, token);
    }
}
