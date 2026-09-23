namespace StudioX.Engine;

using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Foundation;

public sealed class BuildMemoryService
{
    internal const string SnapshotPath = ".build/studiox-memory.json";
    private const int AnalysisVersion = 3;
    public Task<BuildMemoryReport> ReadAsync(string directory, CancellationToken token = default) =>
        Task.Run(() => ReadCoreAsync(Path.GetFullPath(directory), token), token);

    private static async Task<BuildMemoryReport> ReadCoreAsync(string root, CancellationToken token)
    {
        try
        {
            var project = await ProjectService.ReadAsync(root, token);
            var snapshotPath = PathBoundary.Resolve(root, SnapshotPath);
            // 小型结果缓存只属于此次构建。仅配置 CMake 不清除它；实际编译开始时即清除，失败后不回显旧百分比。
            if (File.Exists(snapshotPath))
            {
                var snapshot = await JsonStore.ReadAsync<MemorySnapshot>(snapshotPath, token);
                if (snapshot.AnalysisVersion == AnalysisVersion && snapshot.Project == project && snapshot.Inputs.All(input => input.Matches(root))) return snapshot.Report;
            }
            var receiptPath = PathBoundary.Resolve(root, BuildReceipt.RelativePath);
            if (!File.Exists(receiptPath)) return new([], "编译成功后显示各存储区占用。");
            var receipt = await JsonStore.ReadAsync<BuildReceipt>(receiptPath, token);
            if (receipt.Project != project) return new([], "工程配置已变化，请重新编译。");
            if (project.ToolsetId == "stc.sdcc")
            {
                var mem = Path.Combine(root, ".build", "firmware.mem");
                if (!File.Exists(mem)) return new([], "SDCC 构建缺少 .mem 统计文件。");
                var stamp = MemoryInput.Capture(root, mem);
                var regions = new List<BuildMemoryRegion>();
                foreach (var line in await File.ReadAllLinesAsync(mem, token))
                {
                    var name = line.TrimStart().StartsWith("ROM/EPROM/FLASH", StringComparison.Ordinal) ? "程序 Flash"
                        : line.TrimStart().StartsWith("EXTERNAL RAM", StringComparison.Ordinal) ? "扩展 RAM" : null;
                    if (name is null) continue;
                    var match = Regex.Match(line, @"(?<used>[0-9]+)\s+(?<max>[0-9]+)\s*$", RegexOptions.CultureInvariant);
                    if (!match.Success) continue;
                    regions.Add(new(name, 0, ulong.Parse(match.Groups["max"].Value), ulong.Parse(match.Groups["used"].Value)));
                }
                if (regions.Count == 0) return new([], "SDCC .mem 没有可识别的容量统计。");
                if (!stamp.Matches(root)) return new([], "构建产物正在变化，请稍后重试。");
                var sdccReport = new BuildMemoryReport([new("firmware.ihx", "SDCC", File.GetLastWriteTimeUtc(mem), regions)],
                    "SDCC .mem 统计；片内 idata 与堆栈不在此视图中。");
                await JsonStore.WriteAsync(snapshotPath, new MemorySnapshot(project, sdccReport, [stamp], AnalysisVersion), token);
                return sdccReport;
            }
            var targets = new List<BuildMemoryTarget>();
            var inputs = new List<MemoryInput>();
            var configuration = "";
            var cachePath = Path.Combine(root, ".build", "CMakeCache.txt");
            if (File.Exists(cachePath))
                configuration = (await File.ReadAllLinesAsync(cachePath, token))
                    .FirstOrDefault(line => line.StartsWith("CMAKE_BUILD_TYPE:STRING=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? "";
            foreach (var relative in receipt.Images.Select(image => image.SymbolsPath ?? (image.Format == "elf" ? image.RelativePath : null))
                .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var elf = PathBoundary.Resolve(root, relative);
                var map = Path.ChangeExtension(elf, ".map");
                try
                {
                    var elfStamp = MemoryInput.Capture(root, elf); var mapStamp = MemoryInput.Capture(root, map);
                    var regions = await BuildMemoryAnalyzer.AnalyzeAsync(elf, map, token);
                    if (!elfStamp.Matches(root) || !mapStamp.Matches(root)) throw new IOException("构建产物正在变化，请稍后重新编译。");
                    inputs.Add(elfStamp); inputs.Add(mapStamp);
                    targets.Add(new(Path.GetFileName(elf), configuration, File.GetLastWriteTimeUtc(elf), regions));
                }
                catch (Exception ex) when (IsAnalysisError(ex))
                { targets.Add(new(Path.GetFileName(elf), configuration, DateTime.MinValue, [], ex.Message)); }
            }
            var report = new BuildMemoryReport(targets, targets.Count == 0 ? "没有可分析的 ELF 构建产物。" : "上次成功构建 · 非运行时峰值");
            if (targets.Count > 0 && targets.All(target => target.Diagnostic is null))
            {
                try { await JsonStore.WriteAsync(snapshotPath, new MemorySnapshot(project, report, inputs.ToArray(), AnalysisVersion), token); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { return report with { Message = report.Message + "；统计缓存未保存：" + ex.Message }; }
            }
            return report;
        }
        catch (Exception ex) when (IsAnalysisError(ex)) { return new([], "暂无法分析：" + ex.Message); }
    }
    private static bool IsAnalysisError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or
        StudioXException or OverflowException or FormatException;
    private sealed record MemorySnapshot(ProjectManifest Project, BuildMemoryReport Report, MemoryInput[] Inputs, int AnalysisVersion = 0);
    private sealed record MemoryInput(string RelativePath, long Length, DateTime ModifiedUtc)
    {
        public static MemoryInput Capture(string root, string path)
        {
            var file = new FileInfo(path);
            return new(Path.GetRelativePath(root, path).Replace('\\', '/'), file.Length, file.LastWriteTimeUtc);
        }
        public bool Matches(string root)
        {
            var file = new FileInfo(PathBoundary.Resolve(root, RelativePath));
            return file.Exists && file.Length == Length && file.LastWriteTimeUtc == ModifiedUtc;
        }
    }
}
