namespace StudioX.Engine;

using System.Security.Cryptography;
using StudioX.Foundation;

public static class BuildDetailsService
{
    public static Task<BuildDetails> ReadAsync(string project, CancellationToken token = default) => Task.Run(async () =>
    {
        var root = Path.GetFullPath(project);
        var manifest = await ProjectService.ReadAsync(root, token);
        var receipt = await JsonStore.ReadAsync<BuildReceipt>(PathBoundary.Resolve(root, BuildReceipt.RelativePath), token);
        if (manifest != receipt.Project || receipt.SourceStamp is null || receipt.SourceStamp != await Debugging.DebugSourceStamp.ComputeAsync(root, token))
        {
            throw new StudioXException("BUILD_HISTORY_STALE", "源码或参数与构建记录不同，请重新编译后保存构建快照。");
        }
        var contributions = new List<BuildContribution>();
        foreach (var image in receipt.Images.Where(i => i.SymbolsPath is not null))
        {
            await using var symbols = File.OpenRead(PathBoundary.Resolve(root, image.SymbolsPath!));
            if (!Convert.ToHexString(await SHA256.HashDataAsync(symbols, token)).Equals(image.SymbolsSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new StudioXException("BUILD_HISTORY_IMAGE", "ELF 与构建记录不一致，未保存比较快照。");
            }
            var map = Path.ChangeExtension(PathBoundary.Resolve(root, image.SymbolsPath!), ".map");
            if (!File.Exists(map))
            {
                continue;
            }
            if (new FileInfo(map).Length > 128 * 1024 * 1024)
            {
                throw new StudioXException("BUILD_MAP_SIZE", "MAP 超过 128 MiB。");
            }
            contributions.AddRange(BuildDetailAnalyzer.ParseMap(await File.ReadAllTextAsync(map, token)));
        }
        var log = PathBoundary.Resolve(root, ".build/.ninja_log");
        if (File.Exists(log) && new FileInfo(log).Length > 32 * 1024 * 1024)
        {
            throw new StudioXException("BUILD_LOG_SIZE", "Ninja 日志超过 32 MiB。");
        }
        var timings = File.Exists(log) ? BuildDetailAnalyzer.ParseNinjaLog(await File.ReadAllTextAsync(log, token)) : [];
        return new BuildDetails(manifest, receipt.SourceStamp, string.Join(";", receipt.Images.Select(i => i.SymbolsSha256 ?? i.Sha256)), contributions, timings);
    }, token);
}
