using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Engine;
using StudioX.Foundation;

internal static class EvidenceChecks
{
    internal static async Task RunAsync(string toolsets, string matrixDirectory, string outputDirectory)
    {
        var matrixRoot = Path.GetFullPath(matrixDirectory);
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output)) throw new IOException("Use a new validation output directory.");
        Directory.CreateDirectory(output);
        var timing = new Ag32TimingService(new ToolsetCatalog(Path.GetFullPath(toolsets)));
        var checks = new List<string>();
        void Check(bool value, string description)
        {
            if (!value) throw new InvalidOperationException(description);
            checks.Add(description);
            Console.WriteLine("PASS " + description);
        }
        using var matrix = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(matrixRoot, "matrix.json")));
        foreach (var entry in matrix.RootElement.EnumerateArray())
        {
            var device = entry.GetProperty("device").GetString()!;
            var sys = entry.GetProperty("sysMhz").GetInt32();
            var actual = await timing.ReadAsync(Path.Combine(matrixRoot, device, sys.ToString()));
            var expected = entry.GetProperty("status");
            Check(actual.State.ToString() == expected.GetProperty("state").GetString() &&
                actual.SetupSlackNs == expected.GetProperty("setupSlackNs").GetDecimal() &&
                actual.HoldSlackNs == expected.GetProperty("holdSlackNs").GetDecimal(),
                device + $" {sys}: current reader preserves actual route state and slack");
        }
        Check(Ag32ClockPolicy.Validate("AG32VF303CCT6", new(8, null, 0)) is not null,
            "Explicit BUS=0 still requires SYS for generated initialization");
        Check(Ag32ClockPolicy.Guidance("AG32VF303CCT6", new(8, 160, 0), new(true)).Contains("高于离线推荐范围", StringComparison.Ordinal),
            "Direct SYS bus receives high-frequency guidance");
        var root = Path.Combine(output, "fixture");
        var sourceRoot = Path.Combine(matrixRoot, "AG32VF303CCT6/100");
        foreach (var path in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var target = PathBoundary.Resolve(root, Path.GetRelativePath(sourceRoot, path).Replace('\\', '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target);
        }
        const string attemptPath = ".build/ag32-mapping/studiox-timing-attempt.json";
        const string reportPath = ".build/ag32-mapping/logic_db/setup_summary.rpt.gz";
        var attemptText = await File.ReadAllTextAsync(PathBoundary.Resolve(root, attemptPath));
        await ReplaceAsync(attemptPath, Encoding.UTF8.GetBytes("{"), Ag32TimingState.Unverified, "Malformed record is unverified");
        await ReplaceAsync(attemptPath, Encoding.UTF8.GetBytes("null"), Ag32TimingState.Unverified, "Empty record is unverified");
        await ReplaceAsync(attemptPath, Encoding.UTF8.GetBytes(attemptText.Replace("\"version\": 1", "\"version\": 99", StringComparison.Ordinal)),
            Ag32TimingState.Stale, "Unknown evidence version is stale");
        var reportBytes = await File.ReadAllBytesAsync(PathBoundary.Resolve(root, reportPath));
        using var compressed = new MemoryStream(reportBytes);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var reportText = await reader.ReadToEndAsync();
        await ReplaceAsync(reportPath, Gzip([0xff, 0xfe, 0xff]), Ag32TimingState.Unverified, "Invalid report UTF-8 clears green state");
        var malformed = Regex.Replace(reportText, @"(?m)^(\s*Setup\s+)-?[0-9]+(?:\.[0-9]+)?,",
            match => match.Groups[1].Value + new string('9', 60) + ",");
        Check(malformed != reportText, "Overflow fixture modifies actual setup slack");
        await ReplaceAsync(reportPath, Gzip(Encoding.UTF8.GetBytes(malformed)), Ag32TimingState.Unverified, "Overflow report slack is unverified without crashing the page");
        await ReplaceAsync(reportPath, Gzip(Encoding.UTF8.GetBytes(reportText + "\n# report changed\n")), Ag32TimingState.Stale,
            "Changed report with valid gzip is stale");
        var attemptFile = PathBoundary.Resolve(root, attemptPath);
        var attemptBytes = await File.ReadAllBytesAsync(attemptFile);
        try
        {
            File.Delete(attemptFile);
            Check((await timing.ReadAsync(root)).State == Ag32TimingState.Passed, "Legacy success receipt is checked against real routing before showing green");
        }
        finally { await File.WriteAllBytesAsync(attemptFile, attemptBytes); }
        await ReplaceAsync(".build/ag32-mapping/studiox-mapping-receipt.json", Encoding.UTF8.GetBytes("{}"),
            Ag32TimingState.Unverified, "Positive attempt cannot replace a valid download receipt");
        Check((await timing.ReadAsync(root)).State == Ag32TimingState.Passed, "Restored fixture shows the original verified result");
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new { success = true, checks, count = checks.Count, hardwareConnected = false });

        async Task ReplaceAsync(string relative, byte[] bytes, Ag32TimingState expected, string description)
        {
            var path = PathBoundary.Resolve(root, relative);
            var original = await File.ReadAllBytesAsync(path);
            try
            {
                await File.WriteAllBytesAsync(path, bytes);
                Check((await timing.ReadAsync(root)).State == expected, description);
            }
            finally { await File.WriteAllBytesAsync(path, original); }
        }
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(bytes);
        return output.ToArray();
    }
}
