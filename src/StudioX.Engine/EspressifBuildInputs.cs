namespace StudioX.Engine;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>构建凭据只覆盖工程和已锁定工具；外部组件、头文件与库须先复制到工程。</summary>
internal static class EspressifBuildInputs
{
    internal static async Task ValidateConfigurationAsync(string root, ResolvedToolset tools, EspressifProjectSettings sdk, CancellationToken token)
    {
        var build = PathBoundary.Resolve(root, ".build");
        var boundary = new InputBoundary(root, tools.RootDirectory);
        _ = await CMakeFileApi.ExecutablesAsync(build, tools.ForEspressifTarget(sdk.Target), token);
        using var description = await ReadAsync(PathBoundary.Resolve(build, "project_description.json"), token);
        foreach (var field in new[] { "config_file", "config_defaults", "build_component_paths" })
        {
            if (!description.RootElement.TryGetProperty(field, out var paths)) { continue; }
            var values = paths.ValueKind == JsonValueKind.Array ? paths.EnumerateArray().Select(item => item.GetString())
                : (paths.GetString() ?? "").Split(';');
            foreach (var path in values.Where(path => !string.IsNullOrWhiteSpace(path))) { boundary.Allowed(path!, root); }
        }
        var reply = PathBoundary.Resolve(build, ".cmake/api/v1/reply");
        var index = new DirectoryInfo(reply).EnumerateFiles("index-*.json").MaxBy(file => file.LastWriteTimeUtc)
            ?? throw new StudioXException("ESPRESSIF_INPUTS", "原生构建未提供输入目录描述。");
        using var indexJson = await ReadAsync(index.FullName, token);
        var response = indexJson.RootElement.GetProperty("reply").GetProperty("client-studiox");
        using var model = await ReadAsync(PathBoundary.Resolve(reply, response.GetProperty("codemodel-v2").GetProperty("jsonFile").GetString()!), token);
        foreach (var configuration in model.RootElement.GetProperty("configurations").EnumerateArray())
        {
            foreach (var target in configuration.GetProperty("targets").EnumerateArray())
            {
                using var targetJson = await ReadAsync(PathBoundary.Resolve(reply, target.GetProperty("jsonFile").GetString()!), token);
                var value = targetJson.RootElement;
                if (value.TryGetProperty("sources", out var sources))
                {
                    foreach (var source in sources.EnumerateArray()) { boundary.Allowed(source.GetProperty("path").GetString()!, root); }
                }
                if (value.TryGetProperty("compileGroups", out var groups))
                {
                    foreach (var group in groups.EnumerateArray().Where(group => group.TryGetProperty("includes", out _)))
                    {
                        foreach (var include in group.GetProperty("includes").EnumerateArray()) { boundary.Allowed(include.GetProperty("path").GetString()!, root); }
                    }
                }
                if (value.TryGetProperty("link", out var link) && link.TryGetProperty("commandFragments", out var fragments))
                {
                    foreach (var fragment in fragments.EnumerateArray())
                    {
                        var arguments = Regex.Matches(fragment.GetProperty("fragment").GetString()!, "\"[^\"]*\"|[^\\s]+")
                            .Select(match => match.Value.Trim('"')).ToArray();
                        for (var i = 0; i < arguments.Length; i++)
                        {
                            var argument = arguments[i];
                            if (argument is "-L" or "-T" && i + 1 < arguments.Length) { boundary.Allowed(arguments[++i], build); }
                            else if (argument.StartsWith("-L", StringComparison.Ordinal) && argument.Length > 2) { boundary.Allowed(argument[2..], build); }
                            else if (argument.StartsWith("-Wl,", StringComparison.Ordinal))
                            {
                                foreach (var part in argument[4..].Split(',').Where(Path.IsPathFullyQualified)) { boundary.Allowed(part, build); }
                            }
                            else if (!argument.StartsWith('-') && Path.GetExtension(argument).ToLowerInvariant() is ".a" or ".lib" or ".o" or ".obj" or ".ld")
                            { boundary.Allowed(argument, build); }
                        }
                    }
                }
            }
        }
    }

    internal static async Task ValidateDependenciesAsync(string root, ResolvedToolset tools,
        Dictionary<string, string> environment, CancellationToken token)
    {
        var build = PathBoundary.Resolve(root, ".build");
        var boundary = new InputBoundary(root, tools.RootDirectory);
        var lines = new StringBuilder();
        StudioXException? invalid = null;
        var count = 0;
        var gate = new object();
        var dependencyTree = true;
        var currentGraph = build;
        var output = new StreamOutput(chunk =>
        {
            lock (gate)
            {
                if (invalid is not null) { return; }
                lines.Append(chunk);
                while (lines.ToString().IndexOf('\n') is var end && end >= 0)
                {
                    token.ThrowIfCancellationRequested();
                    var line = lines.ToString(0, end).TrimEnd('\r');
                    lines.Remove(0, end + 1);
                    if (string.IsNullOrWhiteSpace(line) || dependencyTree && !line.StartsWith("    ", StringComparison.Ordinal)) { continue; }
                    count++;
                    try { boundary.Allowed(line.Trim().Trim('"'), currentGraph); }
                    catch (StudioXException exception) { invalid = exception; lines.Clear(); return; }
                }
                if (lines.Length > 32768)
                {
                    invalid = new StudioXException("ESPRESSIF_INPUTS", "Ninja 的输入记录超过了合法路径行长度。");
                    lines.Clear();
                }
            }
        });
        foreach (var graph in new[] { build, PathBoundary.Resolve(build, "bootloader") }.Where(path => File.Exists(Path.Combine(path, "build.ninja"))))
        {
            currentGraph = graph;
            foreach (var mode in new[] { "deps", "inputs" })
            {
                dependencyTree = mode == "deps";
                count = 0;
                var arguments = new List<string> { "-C", EspressifNativePath.For(graph), "-t", mode };
                if (mode == "inputs") { arguments.Add("all"); }
                var result = await new ProcessRunner().RunAsync(new(tools.Tool("ninja"), arguments,
                    EspressifNativePath.For(root), TimeSpan.FromMinutes(2), environment, RemoveEnvironment: ToolsetEnvironment.AmbientVariables,
                    Output: output, StreamCompleteOutput: true), token);
                // 依赖树会超过日志缓存上限；流式观察器检查完整输出，inputs 另覆盖嵌入资源与预编库。
                if (invalid is not null) { throw invalid; }
                if (!result.Success || count == 0 || lines.Length > 0)
                { throw new StudioXException("ESPRESSIF_INPUTS", "无法完整核对原生编译依赖：" + result.StandardError); }
            }
        }
        if (invalid is not null) { throw invalid; }
    }

    private sealed class InputBoundary(string root, string tools)
    {
        private readonly string[] owners = [EspressifPathIdentity.NormalizePath(root), EspressifPathIdentity.NormalizePath(tools)];
        private readonly HashSet<string> checkedPaths = new(StringComparer.OrdinalIgnoreCase);
        internal void Allowed(string path, string baseDirectory)
        {
            var full = Path.GetFullPath(path, baseDirectory);
            if (checkedPaths.Contains(full)) { return; }
            var absolute = EspressifPathIdentity.NormalizePath(full);
            foreach (var owner in owners)
            {
                if (absolute.Equals(owner, StringComparison.OrdinalIgnoreCase)) { checkedPaths.Add(full); return; }
                if (absolute.StartsWith(owner.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    _ = PathBoundary.Resolve(owner, Path.GetRelativePath(owner, absolute).Replace('\\', '/'));
                    checkedPaths.Add(full);
                    return;
                }
            }
            throw new StudioXException("ESPRESSIF_EXTERNAL_INPUT", "构建输入不属于本工程或已锁定 SDK，不能生成下载凭据：" + path +
                "。请把组件、头文件、资源或预编库复制到工程内，或使用已锁定的 SDK/工具组件。");
        }
    }
    private static async Task<JsonDocument> ReadAsync(string path, CancellationToken token) => JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
    private sealed class StreamOutput(Action<string> action) : IProgress<string> { public void Report(string value) => action(value); }
}
