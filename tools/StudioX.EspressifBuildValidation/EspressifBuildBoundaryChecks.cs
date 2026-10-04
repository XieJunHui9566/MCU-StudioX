using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;

internal static class EspressifBuildBoundaryChecks
{
    internal static async Task<int> RunAsync(ToolsetCatalog catalog, string projects, string output)
    {
        var source = Path.Combine(projects, "esp32c3_hello_world");
        var root = Path.Combine(output, "project with spaces");
        CopyProject(root);
        var manifest = await ProjectService.ReadAsync(root);
        var componentDirectory = manifest.EntryFile is { } entryFile ?
            Path.GetDirectoryName(entryFile.Replace('\\', '/'))?.Replace('\\', '/') ?? "src" : "src";
        var componentCMake = PathBoundary.Resolve(root, componentDirectory + "/CMakeLists.txt");
        var builds = new BuildService(catalog);
        var results = new List<string>();
        await builds.SaveSettingsAsync(root, new(Optimization: CompilerOptimization.O0, DebugInfo: CompilerDebugInfo.Full));
        var configured = await builds.ConfigureAsync(root);
        if (!configured.Success)
        {
            throw new InvalidOperationException(configured.Log);
        }
        Check(true, "Actual -O0/-g3 native compile commands verified");
        Check(File.Exists(Path.Combine(root, ".build/compile_commands.json")), "Native database generated for a project path containing spaces");
        Check(!File.Exists(Path.Combine(root, ".build/studiox-build-receipt.json")), "Configure does not authorize stale images");
        var cmakePath = Path.Combine(root, "CMakeLists.txt");
        var original = await File.ReadAllTextAsync(cmakePath);
        await File.AppendAllTextAsync(cmakePath, "\nmessage(FATAL_ERROR \"StudioX intentional native configuration failure\")\n");
        await SeedReceiptAsync();
        var failed = await builds.ConfigureAsync(root);
        Check(!failed.Success && failed.Log.Contains("intentional native configuration failure", StringComparison.Ordinal), "Native failure retains original CMake diagnostic");
        Check(!File.Exists(Path.Combine(root, ".build/studiox-build-receipt.json")), "Native failure removes previous receipt");
        await File.WriteAllTextAsync(cmakePath, original);
        await SeedReceiptAsync();
        using var cancellation = new CancellationTokenSource();
        Task<BuildReport>? competing = null;
        var progress = new ProgressAction(message =>
        {
            if (message == "配置 esp-idf")
            {
                competing = builds.ConfigureAsync(root);
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(250));
            }
        });
        try
        {
            await builds.ConfigureAsync(root, progress, cancellation.Token);
            throw new InvalidOperationException("Expected native configuration cancellation");
        }
        catch (OperationCanceledException)
        {
            Check(!File.Exists(Path.Combine(root, ".build/studiox-build-receipt.json")), "Cancellation removes previous receipt");
            Check((await File.ReadAllTextAsync(Path.Combine(root, ".build/studiox-build.log"))).Contains("OperationCanceledException", StringComparison.Ordinal), "Cancellation preserves raw build log");
        }
        if (competing is null)
        {
            throw new InvalidOperationException("Concurrent operation was not started");
        }
        try
        {
            await competing;
            throw new InvalidOperationException("Expected concurrent build rejection");
        }
        catch (StudioXException exception) when (exception.Code == "BUILD_BUSY") { Check(true, "Concurrent native operation rejected by build ownership gate"); }
        await builds.SaveSettingsAsync(root, new());
        var resource = PathBoundary.Resolve(root, componentDirectory + "/stamp-resource.bin");
        await File.WriteAllBytesAsync(resource, [1, 2, 3, 4]);
        await File.AppendAllTextAsync(componentCMake,
            "\ntarget_add_binary_data(${COMPONENT_LIB} \"${CMAKE_CURRENT_LIST_DIR}/stamp-resource.bin\" BINARY)\n");
        var embedded = await builds.BuildAsync(root);
        if (!embedded.Success)
        {
            throw new InvalidOperationException(embedded.Log);
        }
        var flash = new EspressifFlashService(catalog);
        _ = await flash.PreviewAsync(root, new(Port: "COM999"));
        Check(true, "Real embedded binary build produces a complete read-only flash preview");
        await File.WriteAllBytesAsync(resource, [4, 3, 2, 1]);
        try
        {
            await flash.PreviewAsync(root, new(Port: "COM999"));
            throw new InvalidOperationException("Expected embedded asset modification rejection");
        }
        catch (StudioXException exception) when (exception.Code == "ESP_FLASH_BUILD") { Check(true, "Changing only an embedded .bin resource invalidates the receipt without opening hardware"); }
        var external = Path.Combine(output, "external-include");
        Directory.CreateDirectory(external);
        await File.AppendAllTextAsync(componentCMake,
            "\ntarget_include_directories(${COMPONENT_LIB} PRIVATE \"" + external.Replace('\\', '/') + "\")\n");
        try
        {
            await builds.ConfigureAsync(root);
            throw new InvalidOperationException("Expected untracked external input rejection");
        }
        catch (StudioXException exception) when (exception.Code == "ESPRESSIF_EXTERNAL_INPUT")
        {
            Check(!File.Exists(Path.Combine(root, ".build/studiox-build-receipt.json")), "Canonical external include directory is rejected without leaving a download receipt");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        {
            count = results.Count,
            results
        }, JsonStore.Options));
        return 0;

        void CopyProject(string destinationRoot)
        {
            Directory.CreateDirectory(destinationRoot);
            Directory.CreateDirectory(PathBoundary.Resolve(destinationRoot, "include"));
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                .Where(path => !Path.GetRelativePath(source, path).StartsWith(".build" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            {
                var destination = PathBoundary.Resolve(destinationRoot, Path.GetRelativePath(source, file).Replace('\\', '/'));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
        }
        Task SeedReceiptAsync() => File.WriteAllTextAsync(Path.Combine(root, ".build/studiox-build-receipt.json"), "{\"testSentinel\":true}");
        void Check(bool passed, string message)
        {
            if (!passed)
            {
                throw new InvalidOperationException(message);
            }
            results.Add(message);
            Console.WriteLine("PASS " + message);
        }
    }
    private sealed class ProgressAction(Action<string> action) : IProgress<string>
    {
        public void Report(string value) => action(value);
    }
}
