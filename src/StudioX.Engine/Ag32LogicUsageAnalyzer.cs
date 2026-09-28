namespace StudioX.Engine;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>读取成功联合构建中的 Supra 资源统计，不从位流大小或型号名称推测容量。</summary>
public static partial class Ag32LogicUsageAnalyzer
{
    public static async Task<BuildLogicUsage?> AnalyzeAsync(string logPath, CancellationToken token = default)
    {
        if (new FileInfo(logPath).Length > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("Supra 构建日志超过 64 MiB，暂无法读取逻辑单元统计。");
        }
        var text = await File.ReadAllTextAsync(logPath, token);
        // 优先使用布线设计统计；LUT 与寄存器可共享单元，不能相加当作 LE 数量。
        foreach (var (pattern, source) in new[]
        {
            (RouteStatistics(), "Supra · Route Design Statistics"),
            (PlacementStatistics(), "Supra · Placement Statistics"),
            (PackingStatistics(), "Supra · Packing Statistics")
        })
        {
            var matches = pattern.Matches(text);
            if (matches.Count == 0) { continue; }
            var counts = Counts().Match(matches[^1].Groups["counts"].Value);
            if (!counts.Success ||
                !ulong.TryParse(counts.Groups["used"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var used) ||
                !ulong.TryParse(counts.Groups["capacity"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var capacity) || capacity == 0)
            {
                throw new InvalidDataException("Supra 逻辑单元统计格式无效，未推测占用或容量。");
            }
            return new(used, capacity, source);
        }
        return null;
    }

    [GeneratedRegex(@"^\s*Logic[ \t]+Slices[ \t]*:[ \t]*(?<counts>[^\r\n]*)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex RouteStatistics();
    [GeneratedRegex(@"^\s*Total[ \t]+Logic[ \t]+Counts[ \t]*:[ \t]*(?<counts>[^\r\n]*)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PlacementStatistics();
    [GeneratedRegex(@"^\s*Total[ \t]+Logics[ \t]*:[ \t]*(?<counts>[^\r\n]*)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PackingStatistics();
    [GeneratedRegex(@"^(?<used>[0-9]+)[ \t]*/[ \t]*(?<capacity>[0-9]+)[ \t]*(?:\([ \t]*[0-9]+(?:\.[0-9]+)?%\))?[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Counts();
}
