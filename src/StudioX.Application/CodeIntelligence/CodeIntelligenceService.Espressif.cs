namespace StudioX.Application.CodeIntelligence;

using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;

public sealed partial class CodeIntelligenceService
{
    private async Task<Dictionary<string, object>> CreateEspressifDatabaseAsync(string cache,
        EspressifAnalysisProfile profile, CancellationToken token)
    {
        var database = Path.Combine(projectRoot, ".build", "compile_commands.json");
        if (!File.Exists(database))
        {
            StatusDescription = "通用提示已就绪；请先配置或编译，生成 SDK 头文件与宏配置。" + profile.Description;
            log.Enqueue(StatusDescription);
            return await CreateNavigationDatabaseAsync(cache, token).ConfigureAwait(false);
        }
        await using var stream = File.OpenRead(database);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        foreach (var entry in json.RootElement.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var directory = EspressifPathIdentity.NormalizePath(Path.GetFullPath(entry.GetProperty("directory").GetString()!, projectRoot));
            var path = EspressifPathIdentity.NormalizePath(Path.GetFullPath(entry.GetProperty("file").GetString()!, directory));
            if (Path.GetExtension(path).ToLowerInvariant() is not (".c" or ".cpp" or ".cc" or ".cxx"))
            {
                continue;
            }
            var original = entry.TryGetProperty("arguments", out var values)
                ? values.EnumerateArray().Select(value => value.GetString()!).ToArray()
                : SplitCMakeCommand(entry.GetProperty("command").GetString()!);
            var arguments = profile.Translate(original, directory, path, Path.Combine(runtimeDirectory, "languages", "clangd", "bin", "clang.exe"));
            importedCommands.TryAdd(path, new(directory, path, arguments));
        }
        if (importedCommands.Count == 0)
        {
            throw new StudioXException("LANGUAGE_ESPRESSIF_INDEX", "原生 SDK 编译数据库没有 C/C++ 源文件。");
        }
        await JsonStore.WriteAsync(Path.Combine(cache, "compile_commands.json"), importedCommands.Values.Select(item => new
        {
            directory = item.Directory,
            file = item.File,
            arguments = item.Arguments
        }).ToArray(), token).ConfigureAwait(false);
        StatusDescription = "SDK 代码提示已就绪；" + profile.Description;
        log.Enqueue(StatusDescription);
        return importedCommands.ToDictionary(pair => pair.Key,
            pair => (object)new { workingDirectory = pair.Value.Directory, compilationCommand = pair.Value.Arguments }, StringComparer.OrdinalIgnoreCase);
    }
}
