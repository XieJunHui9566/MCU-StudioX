namespace StudioX.Engine.Hdl;

using System.Text.RegularExpressions;
using StudioX.Foundation;

public sealed partial class HdlEditorAnalysis(ToolsetCatalog catalog)
{
    public static bool Supports(string path) => IsSource(path) || IsHeader(path);
    private static bool IsSource(string path) => Path.GetExtension(path).ToLowerInvariant() is ".v" or ".sv";
    private static bool IsHeader(string path) => Path.GetExtension(path).ToLowerInvariant() is ".vh" or ".svh";

    public sealed record Snapshot(string[] Sources, string[] Includes, string[] Defines, IReadOnlyDictionary<string, string> Files);
    public sealed record Finding(string Path, int Line, bool Warning, string Message);
    public sealed record Analysis(Finding[] Findings, bool Complete, string Status);

    public async Task<Snapshot> CaptureAsync(string? root, string path, string text, IReadOnlyDictionary<string, string> drafts, CancellationToken token)
    {
        path = Normalize(path);
        var sources = IsSource(path) ? new[] { path } : [];
        string[] includes = [], defines = [];
        if (root is not null)
        {
            var simulation = PathBoundary.Resolve(root, HdlSimulationSettings.RelativePath);
            var build = PathBoundary.Resolve(root, Ag32NativeBuildSettings.RelativePath);
            HdlSchematicSettings? settings = null;
            if (File.Exists(build))

            {
                settings = (await JsonStore.ReadAsync<Ag32NativeBuildSettings>(build, token)).Inputs;
            }
            else if (File.Exists(PathBoundary.Resolve(root, ".studiox/project.json")) &&
                     (await ProjectService.ReadAsync(root, token)).Logic is not null)

            {
                settings = (await new Ag32NativeBuildService(catalog).ReadSettingsAsync(root, token)).Inputs;
            }

            if (File.Exists(simulation))
            {
                var sim = await JsonStore.ReadAsync<HdlSimulationSettings>(simulation, token);
                if (Normalize(sim.TestbenchFile).Equals(path, StringComparison.OrdinalIgnoreCase) ||
                    IsHeader(path) && settings is null ||
                    settings is not null && !settings.Sources.Select(Normalize).Contains(path, StringComparer.OrdinalIgnoreCase) &&
                    sim.Sources.Select(Normalize).Contains(path, StringComparer.OrdinalIgnoreCase))

                {
                    settings = sim.Inputs;
                }
            }

            if (settings is not null)
            {
                includes = settings.IncludeDirectories.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var include in includes)
                {
                    if (include != ".")
                    {
                        PathBoundary.Resolve(root, include);
                    }
                }
                defines = settings.Defines;
                if (defines.Any(define => define is null || !DefinePattern().IsMatch(define)))

                {
                    throw new StudioXException("HDL_DEFINE", "Verilog 分析配置中的宏必须为 NAME 或 NAME=数值。");
                }
                sources = settings.Sources.Select(Normalize).Concat(sources).Where(IsSource).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            else
            {
                var folder = Path.GetDirectoryName(PathBoundary.Resolve(root, path))!;
                sources = Directory.EnumerateFiles(folder).Where(IsSource).Order(StringComparer.Ordinal)
                    .Select(file => Relative(root, file)).Concat(sources).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [path] = text };
        long length = text.Length;
        foreach (var file in sources)

        {
            await AddAsync(file);
        }
        if (root is not null)
        {
            // 只读取能够影响本次分析的包含文件。相对路径按 Icarus 的当前文件、工作目录和 -I 顺序解析。
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<string>(files.Keys);
            while (pending.TryDequeue(out var relative))
            {
                if (!visited.Add(relative))
                {
                    continue;
                }
                foreach (Match match in IncludePattern().Matches(files[relative]))
                {
                    token.ThrowIfCancellationRequested();
                    if (!match.Groups["file"].Success)
                    {
                        continue;
                    }
                    var name = Normalize(match.Groups["file"].Value);
                    if (Path.IsPathRooted(name) || name.Contains(':') || name.Split('/').Contains(".."))

                    {
                        throw new StudioXException("HDL_INCLUDE", "编辑器分析使用工程内的包含文件：" + name);
                    }
                    var local = Path.GetDirectoryName(relative) ?? "";
                    foreach (var directory in new[] { local, "." }.Concat(includes))
                    {
                        var candidate = Path.GetFullPath(Path.Combine(root, directory, name));
                        var included = Relative(root, candidate);
                        if (included is ".." || included.StartsWith("../", StringComparison.Ordinal))
                        {
                            continue;
                        }
                        if (!await AddAsync(included))
                        {
                            continue;
                        }
                        pending.Enqueue(included);
                        break;
                    }
                }
            }
        }
        return new(sources.Where(files.ContainsKey).ToArray(), includes, defines, files);

        async Task<bool> AddAsync(string relative)
        {
            if (files.ContainsKey(relative))
            {
                return true;
            }
            var full = root is null ? null : PathBoundary.Resolve(root, relative);
            var fromEditor = drafts.TryGetValue(relative, out var value);
            if (!fromEditor && (full is null || !File.Exists(full)))
            {
                return false;
            }
            if (files.Count >= 256 || (fromEditor ? value!.Length : new FileInfo(full!).Length) > 16 * 1024 * 1024 - length)

            {
                throw new StudioXException("HDL_EDITOR_SIZE", "实时 Verilog 分析的源文件或包含文件超出大小上限。");
            }
            value = fromEditor ? value! : await File.ReadAllTextAsync(full!, token);
            length += value.Length;
            files.Add(relative, value);
            return true;
        }
    }

    public async Task<Analysis> AnalyzeAsync(Snapshot snapshot, string path, CancellationToken token)
    {
        if (snapshot.Sources.Length == 0)

        {
            return new([], false, "头文件需由 Verilog 源文件引用");
        }
        var tools = await catalog.ResolveAsync("hdl.iverilog", "14.0.0", "iverilog", token);
        var library = IcarusToolPath.ForLibraryDirectory(tools.ResourceDirectory("ivl"));
        var encoding = IcarusToolPath.DiagnosticEncoding();
        var work = Directory.CreateTempSubdirectory("studiox-hdl-").FullName;
        try
        {
            foreach (var (relative, text) in snapshot.Files)
            {
                var target = PathBoundary.Resolve(work, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, encoding.GetBytes(text), token);
            }
            var arguments = new List<string> { "-B", library, "-g2012", "-grelative-include", "-i", "-tnull",
                "-Wimplicit", "-Wportbind", "-Wselect-range", "-Minclude=includes.txt", "-o", "check.out" };
            foreach (var directory in snapshot.Includes)

            {
                arguments.AddRange(["-I", directory == "." ? "." : "./" + directory]);
            }
            foreach (var define in snapshot.Defines)

            {
                arguments.AddRange(["-D", define]);
            }
            arguments.AddRange(snapshot.Sources.Select(relative => "./" + relative));
            var result = await new ProcessRunner().RunAsync(new(tools.Tool("iverilog"), arguments, work, TimeSpan.FromSeconds(6),
                ContainProcessTree: true)
            {
                OutputEncoding = encoding
            }, token);
            var output = result.StandardError + "\n" + result.StandardOutput;
            var findings = new List<Finding>();
            foreach (Match match in DiagnosticPattern().Matches(output))
            {
                var relative = Relative(work, Path.GetFullPath(match.Groups["path"].Value, work));
                if (!snapshot.Files.ContainsKey(relative) || !int.TryParse(match.Groups["line"].Value, out var line))
                {
                    continue;
                }
                var message = match.Groups["message"].Value.Trim();
                var warning = message.StartsWith("warning:", StringComparison.OrdinalIgnoreCase);
                findings.Add(new(relative, line, warning, message));
            }
            if (result.TimedOut)
            {
                return new([], false, "Verilog 分析超时");
            }
            if (!result.Success && findings.All(item => item.Warning))

            {
                throw new StudioXException("HDL_EDITOR_TOOL", output.Trim());
            }
            var active = Normalize(path);
            var dependencies = Path.Combine(work, "includes.txt");
            if (result.Success && IsHeader(active) && (!File.Exists(dependencies) ||
                !(await File.ReadAllLinesAsync(dependencies, encoding, token)).Any(file =>
                    Relative(work, Path.GetFullPath(file, work)).Equals(active, StringComparison.OrdinalIgnoreCase))))

            {
                return new(findings.Distinct().ToArray(), false, "当前头文件尚未被分析的源文件引用");
            }
            return new(findings.Distinct().ToArray(), !result.OutputTruncated, result.OutputTruncated ? "Verilog 诊断输出已截断" : "");
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
    private static string Relative(string root, string path) => Normalize(Path.GetRelativePath(root, path));

    [GeneratedRegex(""""/\*[\s\S]*?(?:\*/|\z)|//[^\r\n]*|`include[ \t]+"(?<file>[^"\r\n]+)"|"(?:\\[^\r\n]|[^"\\\r\n])*["]"""")]
    private static partial Regex IncludePattern();
    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]*(=[A-Za-z0-9_'+-]+)?\z")]
    private static partial Regex DefinePattern();
    [GeneratedRegex(@"(?m)^(?<path>[^\r\n]+?):(?<line>[0-9]+):\s*(?<message>[^\r\n]+)")]
    private static partial Regex DiagnosticPattern();
}
