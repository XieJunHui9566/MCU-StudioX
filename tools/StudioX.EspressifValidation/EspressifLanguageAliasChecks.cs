namespace StudioX.EspressifValidation;

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudioX.Application.CodeIntelligence;
using StudioX.Engine;
using StudioX.Foundation;

internal static class EspressifLanguageAliasChecks
{
    public static async Task RunAsync(string runtime, string sourceProject, string output, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var sourceManifest = await ProjectService.ReadAsync(sourceProject, token);
        var directory = Path.Combine(output, "alias project with spaces");
        if (Directory.Exists(directory))
        {
            throw new IOException("Alias validation requires a new fixture directory.");
        }
        var entryFile = sourceManifest.EntryFile ?? "src/main.c";
        var sourceEntry = PathBoundary.Resolve(sourceProject, entryFile);
        var aliasEntry = PathBoundary.Resolve(directory, entryFile);
        Directory.CreateDirectory(Path.GetDirectoryName(aliasEntry)!);
        await JsonStore.WriteAsync(Path.Combine(directory, ".studiox", "project.json"), sourceManifest with
        {
            Name = "alias_project"
        }, token);
        File.Copy(sourceEntry, aliasEntry);
        var build = Path.Combine(directory, ".build");
        Directory.CreateDirectory(build);
        var shortFile = ShortPath(aliasEntry);
        var shortDirectory = ShortPath(build);
        if (!shortFile.Contains('~') && !shortDirectory.Contains('~'))
        {
            Console.WriteLine("SKIP Windows short-path check: this volume did not assign an 8.3 alias.");
            return;
        }
        var nativeEntries = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(sourceProject, ".build", "compile_commands.json"), token))!.AsArray();
        var application = nativeEntries.Single(entry => EspressifPathIdentity.NormalizePath(entry!["file"]!.GetValue<string>()) ==
            EspressifPathIdentity.NormalizePath(sourceEntry))!.DeepClone();
        application["file"] = shortFile;
        application["directory"] = shortDirectory;
        var database = Path.Combine(build, "compile_commands.json");
        await File.WriteAllTextAsync(database, new JsonArray(application).ToJsonString(new JsonSerializerOptions { WriteIndented = true }), token);
        await using var service = new CodeIntelligenceService(runtime, Path.Combine(output, "alias-cache"));
        await service.StartAsync(directory, token);
        const string text = "#include \"esp_system.h\"\nvoid hint(void) { esp_get_free_heap_si }\n";
        var result = await service.CompleteAsync(entryFile, text,
            text.IndexOf("esp_get_free_heap_si", StringComparison.Ordinal) + "esp_get_free_heap_si".Length, token);
        if (!result.Any(item => item.InsertText.Contains("esp_get_free_heap_size", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("8.3 database did not match the editor's long source path.");
        }
        var analysis = Directory.GetFiles(Path.Combine(output, "alias-cache"), "compile_commands.json", SearchOption.AllDirectories).Single();
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(analysis, token));
        var entry = json.RootElement[0];
        if (entry.GetProperty("file").GetString() != EspressifPathIdentity.NormalizePath(aliasEntry) ||
            entry.GetProperty("directory").GetString() != EspressifPathIdentity.NormalizePath(build))
        {
            throw new InvalidOperationException("Analysis cache must use canonical source and working directory paths.");
        }
        Console.WriteLine("PASS Windows 8.3 database source/directory aliases match long editor paths; SDK API completion remains available.");
    }

    private static string ShortPath(string path)
    {
        var result = new StringBuilder(32768);
        var count = GetShortPathName(path, result, (uint)result.Capacity);
        return count > 0 && count < result.Capacity ? result.ToString() : path;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string path, StringBuilder result, uint capacity);
}
