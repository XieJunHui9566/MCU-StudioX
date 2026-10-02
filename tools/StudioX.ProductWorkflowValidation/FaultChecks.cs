namespace StudioX.ProductWorkflowValidation;

using StudioX.Application;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

internal static class FaultChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var evidence = new FaultEvidence(1, "STM32F407ZGT6", "offline fixture", DateTimeOffset.UtcNow, (1u << 25) | (1u << 15) | (1u << 9), 1u << 30, 0xdeadbeef, 0x40000000, 0x08000100, 0x08000201, "register fixture");
        var report = FaultAnalyzer.Analyze(evidence);
        check(report.Findings.Any(f => f.Contains("DIVBYZERO")) && report.Findings.Any(f => f.Contains("FORCED")) && report.Findings.Any(f => f.Contains("BFARVALID")), "HardFault usage and escalation flags decoded");
        check(!report.Findings.Any(f => f.Contains("DEADBEEF")), "invalid MMFAR is not presented as valid fault address");
        check(report.Addresses.SequenceEqual(new uint[] { 0x08000100, 0x08000201 }), "saved exception PC and LR retained for symbolization");
        report = FaultAnalyzer.Analyze(evidence with { Cfsr = 1u << 10, StackedLr = 0xfffffff9 });
        check(report.Findings.Any(f => f.Contains("不一定")) && !report.Addresses.Contains(0xfffffff9), "imprecise bus error caveat and EXC_RETURN excluded from code addresses");
        var panic = FaultAnalyzer.Analyze(evidence with { Cfsr = null, Hfsr = null, StackedPc = null, StackedLr = null, Raw = "A0: 0x12345678\nGuru Meditation Error: LoadProhibited\nBacktrace: 0x40001000:0x3ffb1000 0x40001020:0x3ffb1020" });
        check(panic.Addresses.SequenceEqual(new uint[] { 0x40001000, 0x40001020 }) && panic.Findings.Any(f => f.Contains("LoadProhibited")), "ESP backtrace extracts PC values without treating SP or register data as code");
        var empty = FaultAnalyzer.Analyze(evidence with { Cfsr = null, Hfsr = null, StackedPc = null, StackedLr = null, Raw = "unknown" });
        check(empty.Addresses.Count == 0 && empty.Findings.Any(f => f.Contains("未找到")), "unknown log remains unknown and raw evidence preserved");
        await using var debugger = new DebugSessionService(Path.Combine(root, "fault-data"));
        var service = new FaultAnalysisService(new ToolsetCatalog(Path.Combine(root, "no-tools")), debugger);
        var file = Path.Combine(root, "fault.json");
        await service.ExportAsync(FaultAnalyzer.Analyze(evidence with { FirmwareMatched = true, Source = "hardware claimed by file" }), file);
        var imported = await service.ImportAsync(file);
        check(!imported.Evidence.FirmwareMatched && imported.Evidence.Source.StartsWith("导入报告"), "imported report cannot claim this session verified its firmware");
        try { await service.ReadPausedAsync(); check(false, "disconnected fault reading rejected"); }
        catch (StudioXException) { check(true, "disconnected fault read never creates hardware session"); }
        try { _ = FaultAnalyzer.Analyze(evidence with { FormatVersion = 99 }); check(false, "unknown fault format"); }
        catch (StudioXException e) { check(e.Code == "FAULT_FORMAT", "unknown fault evidence format rejected"); }
    }
}
