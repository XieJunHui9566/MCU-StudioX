namespace StudioX.Application.CodeIntelligence;

using StudioX.Foundation;

public sealed partial class CodeIntelligenceService
{
    private static bool IsCpp(string path) => Path.GetExtension(path).ToLowerInvariant() is ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hh" or ".hxx";
    private string[] AnalysisCommand(string path) => ImportedCommand(path)?.Arguments ?? new[]
    {
        Path.Combine(runtimeDirectory, "languages", "clangd", "bin", "clang.exe"),
        "-x", IsCpp(path) ? "c++" : "c", IsCpp(path) ? "-std=c++17" : "-std=c17"
    }.Concat(flags).Append(path).ToArray();

    private async Task<Dictionary<string, object>> CreateNavigationDatabaseAsync(string cache, CancellationToken token)
    {
        // 只供语言服务建立跨文件索引，不作为构建配置，不运行编译器。
        // 数据库与索引都位于用户缓存中，避免在工程中生成绝对路径或索引文件。
        var entries = new List<object>();
        var commands = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var directories = new Stack<string>(); directories.Push("");
        var files = new ProjectFileService();
        var visited = 0;
        while (directories.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            if (++visited > 10000 || entries.Count >= 5000) { log.Enqueue("工程较大，自动索引已达到范围上限；其他文件打开后仍可分析。"); break; }
            foreach (var item in files.List(projectRoot, directory))
            {
                token.ThrowIfCancellationRequested();
                if (entries.Count >= 5000) { log.Enqueue("自动索引已达到 5000 个源文件上限。"); directories.Clear(); break; }
                if (item.IsLink) continue;
                if (item.IsDirectory)
                {
                    if (!item.Name.StartsWith('.') && item.Name.ToLowerInvariant() is not ("build" or "out" or "bin" or "obj" or "node_modules") && !item.Name.StartsWith("cmake-build-", StringComparison.OrdinalIgnoreCase) && !item.RelativePath.Equals("device/templates", StringComparison.OrdinalIgnoreCase))
                        directories.Push(item.RelativePath);
                    continue;
                }
                if (Path.GetExtension(item.Name).ToLowerInvariant() is not (".c" or ".cpp" or ".cc" or ".cxx")) continue;
                var path = PathBoundary.Resolve(projectRoot, item.RelativePath);
                if (new FileInfo(path).Length > 4 * 1024 * 1024) continue;
                var arguments = AnalysisCommand(path);
                entries.Add(new { directory = projectRoot, file = path, arguments });
                commands[path] = new { workingDirectory = projectRoot, compilationCommand = arguments };
            }
        }
        await JsonStore.WriteAsync(Path.Combine(cache, "compile_commands.json"), entries, token).ConfigureAwait(false);
        return commands;
    }
}
