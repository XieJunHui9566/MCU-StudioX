namespace StudioX.Application.CodeIntelligence;

using System.Text;
using System.Text.Json;
using StudioX.Foundation;

public sealed partial class CodeIntelligenceService
{
    private sealed record ImportedAnalysis(string Directory, string File, string[] Arguments);
    private readonly Dictionary<string, ImportedAnalysis> importedCommands = new(StringComparer.OrdinalIgnoreCase);
    private string AnalysisDirectory(string path) => ImportedCommand(path)?.Directory ?? projectRoot;
    private ImportedAnalysis? ImportedCommand(string path)
    {
        if (importedCommands.TryGetValue(path, out var exact)) return exact;
        if (importedCommands.Count == 0) return null;
        // 头文件沿用相邻翻译单元的宏和头文件路径，不给整个工程拼一套混合参数。
        var source = importedCommands.Values.OrderByDescending(item => Path.GetFileNameWithoutExtension(item.File).Equals(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => CommonDirectoryLength(item.File, path)).First();
        var arguments = source.Arguments.Select(arg => arg.Equals(source.File, StringComparison.OrdinalIgnoreCase) ? path : arg).ToArray();
        // 标准和 ABI 保留原工程设置，只纠正新打开文件的语言。
        return source with { File = path, Arguments = [arguments[0], "-x", IsCpp(path) ? "c++" : "c", ..arguments.Skip(1)] };
    }
    private static int CommonDirectoryLength(string left, string right)
    {
        var a = Path.GetDirectoryName(left)!; var b = Path.GetDirectoryName(right)!;
        var length = 0; while (length < Math.Min(a.Length, b.Length) && char.ToUpperInvariant(a[length]) == char.ToUpperInvariant(b[length])) length++;
        return length;
    }

    private async Task<Dictionary<string, object>> CreateImportedDatabaseAsync(string cache, CancellationToken token)
    {
        var database = Path.Combine(projectRoot, ".build", "compile_commands.json");
        if (!File.Exists(database)) throw new StudioXException("CUBEMX_INDEX", "请先配置或编译 CubeMX 工程，以生成准确的代码提示参数。");
        await using var stream = File.OpenRead(database);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        foreach (var entry in json.RootElement.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var directory = Path.GetFullPath(entry.GetProperty("directory").GetString()!, projectRoot);
            var path = Path.GetFullPath(entry.GetProperty("file").GetString()!, directory);
            if (Path.GetExtension(path).ToLowerInvariant() is not (".c" or ".cpp" or ".cc" or ".cxx")) continue;
            var original = entry.TryGetProperty("arguments", out var values) ? values.EnumerateArray().Select(value => value.GetString()!).ToArray()
                : SplitCMakeCommand(entry.GetProperty("command").GetString()!);
            var args = new List<string> { Path.Combine(runtimeDirectory, "languages/clangd/bin/clang.exe") };
            for (var i = 1; i < original.Length; i++)
            {
                var argument = original[i];
                if (argument is "-o" or "-MF" or "-MT" or "-MQ") { i++; continue; }
                if (argument is "-c" or "-MD" or "-MMD" or "-MP" or "-fstack-usage" or "-fcyclomatic-complexity" or "-fno-tree-loop-distribute-patterns" ||
                    argument.StartsWith("--specs=", StringComparison.Ordinal)) continue;
                if (!argument.StartsWith('-') && Path.GetFullPath(argument, directory).Equals(path, StringComparison.OrdinalIgnoreCase)) continue;
                args.Add(argument);
            }
            args.AddRange(flags); args.Add(path);
            importedCommands.TryAdd(path, new(directory, path, args.ToArray()));
        }
        if (importedCommands.Count == 0) throw new StudioXException("CUBEMX_INDEX", "编译数据库没有 C/C++ 源文件。");
        await JsonStore.WriteAsync(Path.Combine(cache, "compile_commands.json"), importedCommands.Values.Select(item => new
            { directory = item.Directory, file = item.File, arguments = item.Arguments }).ToArray(), token).ConfigureAwait(false);
        return importedCommands.ToDictionary(pair => pair.Key, pair => (object)new { workingDirectory = pair.Value.Directory, compilationCommand = pair.Value.Arguments }, StringComparer.OrdinalIgnoreCase);
    }

    // CMake 在 Windows 导出的 command 使用双引号和反斜线转义；仅拆分参数，不交给 shell 执行。
    private static string[] SplitCMakeCommand(string command)
    {
        var result = new List<string>(); var part = new StringBuilder(); var quoted = false; var started = false;
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (c == '\\')
            {
                var start = i; while (i < command.Length && command[i] == '\\') i++;
                var count = i - start;
                if (i < command.Length && command[i] == '"')
                {
                    part.Append('\\', count / 2);
                    if (count % 2 == 1) part.Append('"'); else quoted = !quoted;
                }
                else { part.Append('\\', count); i--; }
                started = true;
            }
            else if (c == '"') { quoted = !quoted; started = true; }
            else if (char.IsWhiteSpace(c) && !quoted)
            { if (started) { result.Add(part.ToString()); part.Clear(); started = false; } }
            else { part.Append(c); started = true; }
        }
        if (quoted) throw new StudioXException("CUBEMX_COMMAND", "编译数据库中有未闭合的参数引号。");
        if (started) result.Add(part.ToString());
        return result.ToArray();
    }
}
