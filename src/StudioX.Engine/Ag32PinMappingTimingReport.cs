namespace StudioX.Engine;

using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>保留真实 Supra 时序证据；覆盖率与实际余量分别检查，不能以工具退出码代替。</summary>
internal sealed record Ag32PinMappingTimingReport(string Sha256, int Covered, int Total,
    decimal? WorstSetupSlackNs, decimal? WorstHoldSlackNs, string[] Uncovered, Dictionary<string, string> Reports)
{
    internal string Summary => $"时序覆盖：{Covered}/{Total} 条连接；片内预算最小建立余量 {Format(WorstSetupSlackNs)}，保持余量 {Format(WorstHoldSlackNs)}。外部器件采样时序未建模。";

    internal string? SetupPath => CriticalPath("setup_summary", "Setup");
    internal string? HoldPath => CriticalPath("hold_summary", "Hold");
    internal bool Failed => WorstSetupSlackNs < 0 || WorstHoldSlackNs < 0 || Covered != Total;

    internal static async Task<Ag32PinMappingTimingReport> ReadAsync(string build, CancellationToken token, bool requirePassing = true)
    {
        var reports = new Dictionary<string, string>(StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in new[] { "coverage", "setup_summary", "hold_summary" })
        {
            var path = PathBoundary.Resolve(build, "logic_db/" + name + ".rpt.gz");
            if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 8 * 1024 * 1024)
            {
                throw Error("缺少有效的 Supra 时序报告：" + name);
            }
            await using var file = File.OpenRead(path);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var expanded = new MemoryStream();
            var buffer = new byte[8192];
            try
            {
                int count;
                while ((count = await gzip.ReadAsync(buffer, token)) != 0)
                {
                    if (expanded.Length + count > 8 * 1024 * 1024)
                    {
                        throw Error("时序报告解压后超过大小限制。");
                    }
                    expanded.Write(buffer, 0, count);
                }
            }
            catch (InvalidDataException ex) { throw new StudioXException("AG32_MAPPING_TIMING", "时序报告压缩数据损坏：" + name, ex); }
            var text = new UTF8Encoding(false, true).GetString(expanded.ToArray());
            if (!text.Contains("Coverage report", StringComparison.Ordinal))
            {
                throw Error("Supra 时序报告不完整：" + name);
            }
            reports.Add(name, text);
            hash.AppendData(Encoding.UTF8.GetBytes(name + "\0" + text + "\0"));
        }
        var coverage = Regex.Match(reports["coverage"], @"(?m)^\s*User constraints covered ([0-9]+) connections out of ([0-9]+) total,");
        if (!coverage.Success || !int.TryParse(coverage.Groups[1].Value, out var covered) ||
            !int.TryParse(coverage.Groups[2].Value, out var total) || covered > total || total <= 0)
        {
            throw Error("Supra 时序覆盖报告缺少有效的连接数量。");
        }
        var uncovered = Regex.Matches(reports["coverage"], @"(?m)^\s*Uncovered connection:\s*([^\r\n]+)")
            .Select(match => match.Groups[1].Value.Trim()).ToArray();
        if (uncovered.Length != total - covered)
        {
            throw Error("时序报告的未覆盖连接列表与数量不一致。");
        }
        var setup = Slack("setup_summary", "Setup");
        var hold = Slack("hold_summary", "Hold");
        var sdc = await File.ReadAllTextAsync(PathBoundary.Resolve(build, "studiox-clocks.sdc"), token);
        if (sdc.Contains("set_max_delay", StringComparison.Ordinal) && setup is null)
        {
            throw Error("已生成片内布线预算，但 Supra 没有返回对应的建立时序结果。");
        }
        if (sdc.Contains("# AGM analog_ip:", StringComparison.Ordinal) && (setup is null || hold is null))
        {
            throw Error("模拟 IP 缺少可分析的建立或保持时序结果。");
        }
        if (requirePassing && covered != total)
        {
            throw Error($"Supra 时序约束未覆盖 {total - covered} 条连接；请完善约束后重新编译。");
        }
        if (requirePassing && (setup < 0 || hold < 0))
        {
            throw Error($"Supra 时序预算未满足：建立余量 {Format(setup)}，保持余量 {Format(hold)}；请调整频率或映射后重新编译。原始报告位于 {build}。");
        }
        return new(Convert.ToHexString(hash.GetHashAndReset()), covered, total, setup, hold, uncovered, reports);

        decimal? Slack(string report, string kind)
        {
            var values = Regex.Matches(reports[report], @"(?m)^\s*" + kind + @"\s+(-?[0-9]+(?:\.[0-9]+)?),")
                .Select(match => decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
            return values.Length == 0 ? null : values.Min();
        }
    }

    private string? CriticalPath(string report, string kind) => Regex.Matches(Reports[report],
        @"(?m)^\s*" + kind + @"\s+(-?[0-9]+(?:\.[0-9]+)?),\s*([^\r\n]+)")
        .Select(match => (Slack: decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), Path: match.Groups[2].Value.Trim()))
        .OrderBy(item => item.Slack).Select(item => item.Path).FirstOrDefault();

    internal async Task ExportAsync(string build, CancellationToken token)
    {
        foreach (var (name, text) in Reports)
        {
            await File.WriteAllTextAsync(PathBoundary.Resolve(build, name + ".rpt"), text, token);
        }
        await JsonStore.WriteAsync(PathBoundary.Resolve(build, "studiox-timing.json"), new
        {
            Policy = "basic-mapping-one-bus-system-cycle-v1",
            ExternalDeviceTimingModeled = false,
            Covered,
            Total,
            WorstSetupSlackNs,
            WorstHoldSlackNs,
            Uncovered,
            Sha256
        }, token);
    }

    private static string Format(decimal? value) => value is { } slack ? slack.ToString("0.###", CultureInfo.InvariantCulture) + " ns" : "无可分析路径";
    private static StudioXException Error(string message) => new("AG32_MAPPING_TIMING", message);
}
