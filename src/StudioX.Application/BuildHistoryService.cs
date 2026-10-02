namespace StudioX.Application;

using System.Security.Cryptography;
using System.Text;
using StudioX.Engine;
using StudioX.Foundation;

public sealed class BuildHistoryService(string dataDirectory, BuildMemoryService memory)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string Store(string project) => Path.Combine(dataDirectory, "build-history", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(project).ToUpperInvariant()))) + ".json");
    public async Task<IReadOnlyList<BuildHistorySnapshot>> ListAsync(string project, CancellationToken token = default)
    {
        var file = Store(project);
        return File.Exists(file) ? await JsonStore.ReadAsync<BuildHistorySnapshot[]>(file, token) : [];
    }
    public async Task<BuildHistorySnapshot> CaptureAsync(string project, double? seconds = null, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            var details = await BuildDetailsService.ReadAsync(project, token);
            var report = await memory.ReadAsync(project, token);
            if (report.Targets.Count == 0 || report.Targets.Any(t => t.Diagnostic is not null))
            {
                throw new StudioXException("BUILD_HISTORY_MEMORY", "本次构建没有完整静态内存统计，未保存比较快照。");
            }
            var previous = await ListAsync(project, token);
            var snapshot = new BuildHistorySnapshot(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, details, report, seconds);
            await JsonStore.WriteAsync(Store(project), previous.Append(snapshot).TakeLast(20).ToArray(), token);
            return snapshot;
        }
        finally { gate.Release(); }
    }
    public static IReadOnlyList<BuildComparisonRow> Compare(BuildHistorySnapshot before, BuildHistorySnapshot after)
    {
        if (before.Details.Project.DeviceId != after.Details.Project.DeviceId || before.Details.Project.ToolsetId != after.Details.Project.ToolsetId)
        {
            throw new StudioXException("BUILD_COMPARE_TARGET", "器件或工具系列不同，不能直接比较。");
        }
        var rows = new List<BuildComparisonRow>();
        void Add(string category, IEnumerable<(string Name, long Value)> left, IEnumerable<(string Name, long Value)> right)
        {
            var a = left.GroupBy(v => v.Name).ToDictionary(g => g.Key, g => g.Sum(v => v.Value));
            var b = right.GroupBy(v => v.Name).ToDictionary(g => g.Key, g => g.Sum(v => v.Value));
            rows.AddRange(a.Keys.Union(b.Keys).Select(k => new BuildComparisonRow(category, k, a.GetValueOrDefault(k), b.GetValueOrDefault(k))));
        }
        Add("存储区/字节", before.Memory.Targets.SelectMany(t => t.Regions.Select(r => (t.Name + "/" + r.Name, checked((long)r.Used)))), after.Memory.Targets.SelectMany(t => t.Regions.Select(r => (t.Name + "/" + r.Name, checked((long)r.Used)))));
        Add("符号/字节", before.Details.Contributions.Select(c => (c.Category + "/" + c.Name + " · " + c.File, c.Bytes)), after.Details.Contributions.Select(c => (c.Category + "/" + c.Name + " · " + c.File, c.Bytes)));
        Add("文件/字节", before.Details.Contributions.Select(c => (c.File, c.Bytes)), after.Details.Contributions.Select(c => (c.File, c.Bytes)));
        Add("编译步骤/毫秒", before.Details.Timings.Select(c => (c.File, c.Milliseconds)), after.Details.Timings.Select(c => (c.File, c.Milliseconds)));
        return rows.OrderBy(r => r.Category switch { "存储区/字节" => 0, "文件/字节" => 1, "符号/字节" => 2, _ => 3 })
            .ThenByDescending(r => Math.Abs(r.Difference)).ToArray();
    }
    public Task ExportAsync(BuildHistorySnapshot before, BuildHistorySnapshot after, string file, CancellationToken token = default)
        => JsonStore.WriteAsync(file, new
        {
            before,
            after,
            comparison = Compare(before, after)
        }, token);
}
