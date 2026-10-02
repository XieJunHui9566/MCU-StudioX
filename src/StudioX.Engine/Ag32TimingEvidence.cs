namespace StudioX.Engine;

using System.Security.Cryptography;
using StudioX.Foundation;

/// <summary>失败报告也绑定当次输入；此记录永远不能代替可下载镜像的成功凭据。</summary>
internal sealed record Ag32TimingEvidence(int Version, string ProjectSha256, string SourceSha256,
    string ToolFingerprint, string ReportSha256, Dictionary<string, string> Inputs)
{
    internal const string RelativePath = ".build/ag32-mapping/studiox-timing-attempt.json";
    private static readonly string[] InputNames = ["pins.ve", "pins.vx", "pins.hx", "pins.vex", "pins_routed.v", "studiox-clocks.sdc"];

    internal static async Task WriteAsync(string root, string sourceHash, string toolFingerprint,
        Ag32PinMappingTimingReport report, CancellationToken token)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in InputNames)
            hashes[name] = await HashAsync(PathBoundary.Resolve(root, ".build/ag32-mapping/" + name), token);
        var evidence = new Ag32TimingEvidence(1, await HashAsync(PathBoundary.Resolve(root, ".studiox/project.json"), token),
            sourceHash, toolFingerprint, report.Sha256, hashes);
        await JsonStore.WriteAsync(PathBoundary.Resolve(root, RelativePath), evidence, token);
    }

    internal async Task<bool> InputsMatchAsync(string root, CancellationToken token)
    {
        if (Inputs is null || Inputs.Count != InputNames.Length) return false;
        foreach (var name in InputNames)
        {
            if (!Inputs.TryGetValue(name, out var expected)) return false;
            var path = PathBoundary.Resolve(root, ".build/ag32-mapping/" + name);
            if (!File.Exists(path) || await HashAsync(path, token) != expected) return false;
        }
        return true;
    }

    internal static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    internal static void Clear(string root)
    {
        var path = PathBoundary.Resolve(root, RelativePath);
        if (File.Exists(path)) File.Delete(path);
    }
}
