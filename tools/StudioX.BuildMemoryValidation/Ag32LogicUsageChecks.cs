using StudioX.Engine;
using StudioX.Foundation;

internal static class Ag32LogicUsageChecks
{
    internal static async Task RunAsync(string root, ProjectManifest project, Action<bool, string> check)
    {
        var logPath = Path.Combine(root, ".build", "studiox-build.log");
        var service = new BuildMemoryService();
        var snapshotPath = Path.Combine(root, ".build", "studiox-memory.json");
        var receiptPath = Path.Combine(root, ".build", "studiox-build-receipt.json");
        const string statistics = """
            Packing Statistics
             Total      Logics : 7/2112 (  0%)
             Total        LUTs : 6/2112 (  0%)
             Total   Registers : 4/2112 (  0%)
            Placement Statistics
             Total  Logic    Counts  : 4/2112 (0.1%)
            Route Design Statistics
             Logic       Slices : 3/2112 (0.1%)
            """;
        await File.WriteAllTextAsync(logPath, statistics);
        var usage = await Ag32LogicUsageAnalyzer.AnalyzeAsync(logPath);
        check(usage is { Used: 3, Capacity: 2112 } && Math.Abs(usage.Percent - 3 * 100d / 2112) < 0.000001 &&
            usage.Source.Contains("Route", StringComparison.Ordinal), "final Supra count takes priority; no LUT/FF sum or rounded vendor percentage");
        await File.WriteAllTextAsync(logPath, " Total  Logic Counts : 5/4096 (0.1%)\r\n");
        check(await Ag32LogicUsageAnalyzer.AnalyzeAsync(logPath) is { Used: 5, Capacity: 4096 }, "placement fallback reads reported capacity without a chip constant");
        await File.WriteAllTextAsync(logPath, " Total      Logics : 0/2112 (  0%)\n");
        check(await Ag32LogicUsageAnalyzer.AnalyzeAsync(logPath) is { Used: 0, Capacity: 2112 }, "packing fallback preserves real zero usage");
        await File.WriteAllTextAsync(logPath, " Total LUTs : 2/2112 (0%)\n Total Registers : 3/2112 (0%)\n");
        check(await Ag32LogicUsageAnalyzer.AnalyzeAsync(logPath) is null, "missing logic count stays unknown, not zero");
        foreach (var invalid in new[] { "2/0", "-1/2112", "18446744073709551616/2112", "unknown" })
        {
            await File.WriteAllTextAsync(logPath, statistics + "\n Logic Slices : " + invalid);
            try
            {
                await Ag32LogicUsageAnalyzer.AnalyzeAsync(logPath);
                throw new InvalidOperationException("Malformed final statistic accepted: " + invalid);
            }
            catch (InvalidDataException) { check(true, "malformed final count rejects stale earlier data: " + invalid); }
        }
        project = project with
        {
            DeviceId = "AG32VF303CCT6",
            PinMapping = new("AGRV2KL48")
        };
        await JsonStore.WriteAsync(Path.Combine(root, ".studiox", "project.json"), project);
        async Task ReceiptAsync() => await JsonStore.WriteAsync(receiptPath, new
        {
            Project = project,
            ToolFingerprint = "test",
            Images = new[]
        {
            new { RelativePath = ".build/firmware.elf", Format = "elf", Sha256 = "test", SymbolsPath = ".build/firmware.elf" }
        }
        });
        await ReceiptAsync();
        await File.WriteAllTextAsync(logPath, statistics);
        var report = await service.ReadAsync(root);
        check(report.LogicUsage is { Used: 3, Capacity: 2112 } && report.Targets.Count == 1, "joint build exposes memory and Supra usage together");
        File.Delete(receiptPath);
        await File.WriteAllTextAsync(logPath, "CMake configure only");
        check((await service.ReadAsync(root)).LogicUsage is { Used: 3, Capacity: 2112 }, "successful logic snapshot survives reopen/configure replacing the log");
        File.Delete(snapshotPath);
        await ReceiptAsync();
        report = await service.ReadAsync(root);
        check(report.LogicUsage is null && report.LogicDiagnostic is not null && report.Targets.Count == 1,
            "missing Supra statistics preserves MCU memory and reports unknown logic usage");
        try
        {
            await new BuildService(new ToolsetCatalog(Path.Combine(root, "missing-tools"))).BuildAsync(root);
        }
        catch (StudioXException) { }
        report = await service.ReadAsync(root);
        check(report.LogicUsage is null && report.Targets.Count == 0 && !File.Exists(snapshotPath), "failed build clears both logic and memory snapshots");
    }
}
