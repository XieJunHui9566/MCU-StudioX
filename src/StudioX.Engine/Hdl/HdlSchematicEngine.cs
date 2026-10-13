namespace StudioX.Engine.Hdl;

using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Foundation;

/// <summary>在独立进程中运行锁定的 Yosys，将 HDL 快照综合为可查看的 RTL 网表。</summary>
public sealed class HdlSchematicEngine(string runtimeDirectory)
{
    public async Task<HdlSchematicSettings> ReadSettingsAsync(string projectDirectory, CancellationToken token = default)
    {
        var project = await HdlSchematicInputs.RequireProjectAsync(projectDirectory, token);
        var path = PathBoundary.Resolve(projectDirectory, HdlSchematicInputs.SettingsPath);
        if (File.Exists(path))
        {
            return await JsonStore.ReadAsync<HdlSchematicSettings>(path, token);
        }
        var entry = project.Logic!.VerilogFile;
        var include = Path.GetDirectoryName(entry)?.Replace('\\', '/');
        return new(1, HdlSchematicInputs.DiscoverSources(projectDirectory, entry), [string.IsNullOrEmpty(include) ? "." : include], [], "");
    }

    public async Task SaveSettingsAsync(string projectDirectory, HdlSchematicSettings settings, CancellationToken token = default)
    {
        await HdlSchematicInputs.RequireProjectAsync(projectDirectory, token);
        HdlSchematicInputs.Validate(projectDirectory, settings);
        await JsonStore.WriteAsync(PathBoundary.Resolve(projectDirectory, HdlSchematicInputs.SettingsPath), settings, token);
    }

    public async Task<HdlSchematicResult> GenerateAsync(string projectDirectory, HdlSchematicSettings settings,
        CancellationToken token = default, IProgress<string>? progress = null, IProgress<string>? output = null)
    {
        var root = Path.GetFullPath(projectDirectory);
        await HdlSchematicInputs.RequireProjectAsync(root, token);
        HdlSchematicInputs.Validate(root, settings);
        progress?.Report("校验 Yosys 综合工具…");
        var toolRoot = PathBoundary.Resolve(runtimeDirectory, "hdl/yosys");
        var manifestPath = PathBoundary.Resolve(toolRoot, "runtime.json");
        if (!File.Exists(manifestPath))
        {
            throw new StudioXException("HDL_TOOL_MISSING", "缺少内置 Yosys 电路预览组件，请安装完整的 IDE 运行时。");
        }
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, token));
        var executable = PathBoundary.Resolve(toolRoot, manifest.RootElement.GetProperty("executable").GetString()!);
        await using (var stream = File.OpenRead(executable))
        {
            if (Convert.ToHexString(await SHA256.HashDataAsync(stream, token)) != manifest.RootElement.GetProperty("sha256").GetString())
            {
                throw new StudioXException("HDL_TOOL_HASH", "内置 Yosys 的 SHA-256 与运行时清单不一致。");
            }
        }
        var runDirectory = PathBoundary.Resolve(root, ".build/hdl-schematic/" + Guid.NewGuid().ToString("N"));
        var sourceDirectory = Path.Combine(runDirectory, "source");
        Directory.CreateDirectory(sourceDirectory);
        var hashes = await HdlSchematicInputs.SnapshotAsync(root, sourceDirectory, settings, token);
        var netlist = Path.Combine(runDirectory, "netlist.json");
        var log = Path.Combine(runDirectory, "yosys.log");
        var script = Path.Combine(runDirectory, "preview.ys");
        var includeMap = HdlSchematicInputs.PrepareIncludes(sourceDirectory, settings, hashes.Keys, token);
        var includeFlags = string.Join(" ", includeMap.Keys.Select(directory => "-I" + directory));
        var defineFlags = string.Join(" ", settings.Defines.Select(define => "-D" + define));
        var commands = "read_verilog -sv -nodpi " + includeFlags + " " + defineFlags + " " + string.Join(" ", settings.Sources.Select(Quote)) + "\n"
            + "hierarchy -check " + (settings.TopModule.Length == 0 ? "-auto-top" : "-top " + settings.TopModule) + "\n"
            + "proc\nopt\n" + (settings.Flatten ? "flatten\nopt\n" : "")
            + "memory_collect\nopt_clean\ncheck\nwrite_json ../netlist.json\n";
        await File.WriteAllTextAsync(script, commands, token);
        progress?.Report("Verilog 综合与网表生成");
        var result = await new ProcessRunner().RunAsync(new(executable, ["-Q", "-T", "-l", "../yosys.log", "-s", "../preview.ys"],
            sourceDirectory, Timeout.InfiniteTimeSpan, Output: output), token);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "process.log"), result.StandardOutput + "\n" + result.StandardError, token);
        if (!result.Success || !File.Exists(netlist))
        {
            throw new StudioXException("HDL_SYNTHESIS", $"Yosys 综合失败（退出码 {result.ExitCode}）。原始日志：{log}\n"
                + result.StandardError + "\n" + result.StandardOutput);
        }
        if (!await HdlSchematicInputs.IsCurrentAsync(root, hashes, settings, token))
        {
            throw new StudioXException("HDL_SOURCE_CHANGED", "综合期间源码或工程配置发生变化，请重新生成。此次结果未作为当前电路显示。日志：" + log);
        }
        var modules = YosysNetlistReader.Read(await File.ReadAllTextAsync(netlist, token)).Select(module => module with
        {
            Source = RestoreSource(module.Source, includeMap),
            Cells = module.Cells.Select(cell => cell with { Source = RestoreSource(cell.Source, includeMap) }).ToArray(),
        }).ToArray();
        var top = modules.SingleOrDefault(module => module.IsTop)
            ?? throw new StudioXException("HDL_TOP", "网表没有唯一顶层模块，请在预览配置中指定模块名。日志：" + log);
        var report = new HdlSchematicResult(root, top.Name, modules, HdlSchematicInputs.Digest(hashes, settings), hashes,
            netlist, log, manifest.RootElement.GetProperty("version").GetString()!, DateTimeOffset.UtcNow, settings,
            (await File.ReadAllLinesAsync(log, token)).Where(line => line.StartsWith("Warning:", StringComparison.Ordinal)).ToArray());
        await JsonStore.WriteAsync(Path.Combine(runDirectory, "report.json"), report, token);
        return report;
    }

    public Task<bool> IsCurrentAsync(HdlSchematicResult result, CancellationToken token = default) =>
        HdlSchematicInputs.IsCurrentAsync(result.ProjectDirectory, result.InputHashes, result.Settings, token);

    private static string Quote(string value) => "\"" + value.Replace("\\", "/").Replace("\"", "\\\"") + "\"";

    private static string? RestoreSource(string? source, IReadOnlyDictionary<string, string> includes)
    {
        if (source is null)
        {
            return null;
        }
        foreach (var (alias, original) in includes)
        {
            source = source.Replace(alias + "/", original, StringComparison.Ordinal);
        }
        return source;
    }
}
