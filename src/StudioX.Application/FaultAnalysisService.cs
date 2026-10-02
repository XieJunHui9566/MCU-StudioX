namespace StudioX.Application;

using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

public sealed class FaultAnalysisService(ToolsetCatalog tools, DebugSessionService debugger)
{
    private readonly FirmwareFaultService firmware = new(tools);
    public FaultAnalysisReport Analyze(string device, string raw) => FaultAnalyzer.Analyze(new(1, device, "用户粘贴的日志；未连接设备", DateTimeOffset.UtcNow, null, null, null, null, null, null, raw));
    public async Task<FaultAnalysisReport> ReadPausedAsync(CancellationToken token = default) => FaultAnalyzer.Analyze(await debugger.ReadFaultEvidenceAsync(token));
    public Task<FaultAnalysisReport> LocateAsync(string project, FaultAnalysisReport report, CancellationToken token = default) => firmware.SymbolizeAsync(project, report, token);
    public Task<FaultAnalysisReport> DecodeDumpAsync(string project, string file, string type, CancellationToken token = default) => firmware.DecodeCoreDumpAsync(project, file, type, token);
    public async Task<FaultAnalysisReport> ImportAsync(string file, CancellationToken token = default)
    {
        if (new FileInfo(file).Length > 2 * 1024 * 1024)
        {
            throw new StudioXException("FAULT_SIZE", "故障报告超过 2 MiB。");
        }
        var report = await JsonStore.ReadAsync<FaultAnalysisReport>(file, token);
        // 导入文件中的来源字段不能证明本轮实机连接或固件匹配。
        return FaultAnalyzer.Analyze(report.Evidence with
        {
            FirmwareMatched = false,
            Source = "导入报告；原记录来源：" + report.Evidence.Source
        });
    }
    public Task ExportAsync(FaultAnalysisReport report, string file, CancellationToken token = default) => JsonStore.WriteAsync(file, report, token);
}
