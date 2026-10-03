namespace StudioX.Engine;

using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Foundation;

public sealed class BuildMemoryService(ToolsetCatalog? toolsets = null)
{
    internal const string SnapshotPath = ".build/studiox-memory.json";
    private const int AnalysisVersion = 6;
    public Task<BuildMemoryReport> ReadAsync(string directory, CancellationToken token = default, IProgress<string>? progress = null) =>
        Task.Run(() => ReadCoreAsync(Path.GetFullPath(directory), token, progress), token);

    private async Task<BuildMemoryReport> ReadCoreAsync(string root, CancellationToken token, IProgress<string>? progress)
    {
        try
        {
            var project = await ProjectService.ReadAsync(root, token);
            if (project.Kind == ProjectKind.MicroPython)
            {
                return new([], "MicroPython 脚本工程不生成本机 ELF；内存信息可在 REPL 中查询 gc.mem_free()。");
            }
            if (project.Kind == ProjectKind.Zephyr)
            {
                return new([], "Zephyr 实验工程运行时尚未准备，暂无构建内存统计。");
            }
            // 没有构建记录和统计缓存时不启动整套 SDK 自检；已有缓存仍按原规则完整验证。
            var receiptPath = PathBoundary.Resolve(root, BuildReceipt.RelativePath);
            var snapshotPath = PathBoundary.Resolve(root, SnapshotPath);
            if (!File.Exists(receiptPath) && !File.Exists(snapshotPath)) { return new([], "编译成功后显示各存储区占用。"); }
            ResolvedToolset? nativeTools = null;
            string? sourceStamp = null;
            if (project.Espressif is not null)
            {
                if (toolsets is null) { return new([], "SDK 内存统计尚未绑定内置工具目录。"); }
                nativeTools = await toolsets.ResolveAsync(project.ToolsetId, project.ToolsetVersion, project.CompilerId, token, progress: progress);
                sourceStamp = await Debugging.DebugSourceStamp.ComputeAsync(root, token);
            }
            // 小型结果缓存只属于此次构建。仅配置 CMake 不清除它；实际编译开始时即清除，失败后不回显旧百分比。
            if (File.Exists(snapshotPath))
            {
                var snapshot = await JsonStore.ReadAsync<MemorySnapshot>(snapshotPath, token);
                if (snapshot.AnalysisVersion == AnalysisVersion && snapshot.Project == project &&
                    snapshot.ToolFingerprint == nativeTools?.Fingerprint && snapshot.SourceStamp == sourceStamp &&
                    snapshot.Inputs.All(input => input.Matches(root)))
                {
                    return snapshot.Report;
                }
            }
            if (!File.Exists(receiptPath)) { return new([], "编译成功后显示各存储区占用。"); }
            var receipt = await JsonStore.ReadAsync<BuildReceipt>(receiptPath, token);
            if (receipt.Project != project) { return new([], "工程配置已变化，请重新编译。"); }
            if (nativeTools is not null && (receipt.ToolFingerprint != nativeTools.Fingerprint || receipt.SourceStamp != sourceStamp))
            {
                return new([], "工程源码、SDK 配置或开发环境组件已变化，请重新编译后查看统计。");
            }
            if (project.ToolsetId == "stc.sdcc")
            {
                var mem = Path.Combine(root, ".build", "firmware.mem");
                if (!File.Exists(mem))
                {
                    return new([], "SDCC 构建缺少 .mem 统计文件。");
                }
                var stamp = MemoryInput.Capture(root, mem);
                var regions = new List<BuildMemoryRegion>();
                foreach (var line in await File.ReadAllLinesAsync(mem, token))
                {
                    var name = line.TrimStart().StartsWith("ROM/EPROM/FLASH", StringComparison.Ordinal) ? "程序 Flash"
                        : line.TrimStart().StartsWith("EXTERNAL RAM", StringComparison.Ordinal) ? "扩展 RAM" : null;
                    if (name is null)
                    {
                        continue;
                    }
                    var match = Regex.Match(line, @"(?<used>[0-9]+)\s+(?<max>[0-9]+)\s*$", RegexOptions.CultureInvariant);
                    if (!match.Success)
                    {
                        continue;
                    }
                    regions.Add(new(name, 0, ulong.Parse(match.Groups["max"].Value), ulong.Parse(match.Groups["used"].Value)));
                }
                if (regions.Count == 0)
                {
                    return new([], "SDCC .mem 没有可识别的容量统计。");
                }
                if (!stamp.Matches(root))
                {
                    return new([], "构建产物正在变化，请稍后重试。");
                }
                var sdccReport = new BuildMemoryReport([new("firmware.ihx", "SDCC", File.GetLastWriteTimeUtc(mem), regions)],
                    "SDCC .mem 统计；片内 idata 与堆栈不在此视图中。");
                await JsonStore.WriteAsync(snapshotPath, new MemorySnapshot(project, sdccReport, [stamp], AnalysisVersion), token);
                return sdccReport;
            }
            var targets = new List<BuildMemoryTarget>();
            var inputs = new List<MemoryInput>();
            if (nativeTools is not null) { inputs.Add(MemoryInput.Capture(root, receiptPath)); }
            var configuration = "";
            var cachePath = Path.Combine(root, ".build", "CMakeCache.txt");
            if (File.Exists(cachePath))
            {
                configuration = (await File.ReadAllLinesAsync(cachePath, token))
                .FirstOrDefault(line => line.StartsWith("CMAKE_BUILD_TYPE:STRING=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? "";
            }
            foreach (var relative in receipt.Images.Select(image => image.SymbolsPath ?? (image.Format == "elf" ? image.RelativePath : null))
                .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var elf = PathBoundary.Resolve(root, relative);
                var map = Path.ChangeExtension(elf, ".map");
                try
                {
                    var elfStamp = MemoryInput.Capture(root, elf);
                    var mapStamp = MemoryInput.Capture(root, map);
                    var regions = nativeTools is null ? await BuildMemoryAnalyzer.AnalyzeAsync(elf, map, token)
                        : await EspressifMemoryAnalyzer.AnalyzeAsync(root, map, nativeTools, project.Espressif!, token);
                    if (!elfStamp.Matches(root) || !mapStamp.Matches(root))
                    {
                        throw new IOException("构建产物正在变化，请稍后重新编译。");
                    }
                    inputs.Add(elfStamp);
                    inputs.Add(mapStamp);
                    targets.Add(new(Path.GetFileName(elf), configuration, File.GetLastWriteTimeUtc(elf), regions));
                }
                catch (Exception ex) when (IsAnalysisError(ex))
                {
                    targets.Add(new(Path.GetFileName(elf), configuration, DateTime.MinValue, [], ex.Message));
                }
            }
            var report = new BuildMemoryReport(targets, targets.Count == 0 ? "没有可分析的 ELF 构建产物。"
                : project.Espressif is null ? "上次成功构建 · 非运行时峰值"
                : "SDK 静态内存统计 · 共享 RAM 按厂商规则归并，Flash 容量未知；非运行时堆/栈峰值");
            if (Ag32DeviceCatalog.Find(project.DeviceId)?.CanMap == true && (project.PinMapping is not null || project.Logic is not null))
            {
                try
                {
                    var usage = await Ag32LogicUsageAnalyzer.AnalyzeAsync(Path.Combine(root, ".build", "studiox-build.log"), token);
                    report = report with { LogicUsage = usage, LogicDiagnostic = usage is null ? "未取得 Supra 逻辑单元统计，请重新编译。" : null };
                }
                catch (Exception ex) when (IsAnalysisError(ex))
                {
                    report = report with { LogicDiagnostic = "逻辑单元统计不可用：" + ex.Message };
                }
            }
            if (targets.Count > 0 && targets.All(target => target.Diagnostic is null))
            {
                try
                {
                    // 逻辑统计与 ELF/MAP 同属这次成功联合构建；配置 CMake 会重写日志，因此缓存不绑定日志时间戳。
                    await JsonStore.WriteAsync(snapshotPath, new MemorySnapshot(project, report, inputs.ToArray(), AnalysisVersion,
                        nativeTools?.Fingerprint, sourceStamp), token);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return report with
                    {
                        Message = report.Message + "；统计缓存未保存：" + ex.Message
                    };
                }
            }
            return report;
        }
        catch (Exception ex) when (IsAnalysisError(ex)) { return new([], "暂无法分析：" + ex.Message); }
    }
    private static bool IsAnalysisError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
        StudioXException or OverflowException or FormatException;
    private sealed record MemorySnapshot(ProjectManifest Project, BuildMemoryReport Report, MemoryInput[] Inputs, int AnalysisVersion = 0,
        string? ToolFingerprint = null, string? SourceStamp = null);
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
