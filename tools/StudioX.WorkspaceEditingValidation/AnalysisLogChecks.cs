using StudioX.Application.CodeIntelligence;

internal static class AnalysisLogChecks
{
    public static async Task RunAsync(string runtime, string output, Action<bool, string> check)
    {
        Directory.CreateDirectory(output);
        await using var service = new CodeIntelligenceService(runtime, Path.Combine(output, "user-data"));
        for (var index = 0; index < 1000; index++)
        {
            service.RecordAnalysisLog("原始诊断 " + index);
        }
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.FlushAnalysisLogAsync()));
        var raw = await File.ReadAllLinesAsync(service.AnalysisLogPath);
        check(raw.Length == 1000 && raw.Distinct().Count() == 1000 && raw[0].EndsWith("原始诊断 0", StringComparison.Ordinal) &&
            raw[^1].EndsWith("原始诊断 999", StringComparison.Ordinal), "concurrent archival retains more than the former 200-line limit without loss or duplication");
        check(service.AnalysisLogPath.StartsWith(Path.Combine(output, "user-data"), StringComparison.OrdinalIgnoreCase), "language logs are scoped to independent user data");
        service.RecordAnalysisLog("locked-file-retry");
        using (var locked = new FileStream(service.AnalysisLogPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                await service.FlushAnalysisLogAsync();
                throw new InvalidOperationException("Expected locked log rejection.");
            }
            catch (IOException) { check(true, "archive write failures expose the original IO exception"); }
        }
        await service.FlushAnalysisLogAsync();
        check((await File.ReadAllTextAsync(service.AnalysisLogPath)).Contains("locked-file-retry", StringComparison.Ordinal), "failed archive batch survives a later successful retry");
        for (var index = 0; index < 6; index++)
        {
            service.RecordAnalysisLog($"rotation-{index}\n" + new string('x', 2 * 1024 * 1024));
            await service.FlushAnalysisLogAsync();
        }
        var view = await service.ReadAnalysisLogAsync();
        check(Directory.GetFiles(Path.GetDirectoryName(service.AnalysisLogPath)!).Length == 4 && view.Length < 257 * 1024 &&
            (await File.ReadAllTextAsync(service.AnalysisLogPath)).Contains("rotation-5", StringComparison.Ordinal), "session logs rotate to four files and keep the viewer bounded with newest raw records");
    }
}
