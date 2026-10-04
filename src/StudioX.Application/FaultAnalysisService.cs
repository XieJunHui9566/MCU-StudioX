namespace StudioX.Application;

using System.Text.Json;
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
    public Task<FaultAnalysisReport> DecodeDumpAsync(string project, string file, string type, string archivedElf, CancellationToken token = default) => firmware.DecodeCoreDumpAsync(project, file, type, archivedElf, token);
    public async Task<FaultAnalysisReport> ImportAsync(string file, CancellationToken token = default)
    {
        // 导出包含结构化解码和原始诊断；锁住同一输入流，限制在检查后不能被替换或扩大。
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > 16 * 1024 * 1024)
        {
            throw new StudioXException("FAULT_SIZE", "故障报告为空或超过 16 MiB。");
        }
        var report = await JsonSerializer.DeserializeAsync<FaultAnalysisReport>(input, JsonStore.Options, token)
            ?? throw new StudioXException("JSON_EMPTY", "故障报告没有有效内容。");
        // 导入文件中的来源字段不能证明本轮实机连接或固件匹配。
        return report with
        {
            Evidence = report.Evidence with
            {
                FirmwareMatched = false,
                Source = "导入报告；原记录来源：" + report.Evidence.Source
            },
            CoreDump = report.CoreDump is { } dump ? dump with
            {
                HashMatches = false
            } : null,
            Findings = ["导入报告仅保留原记录，未重新验证 ELF 或转储摘要；请重新导入原始转储完成核对。"]
        };
    }
    public Task ExportAsync(FaultAnalysisReport report, string file, CancellationToken token = default) => JsonStore.WriteAsync(file, report, token);
}
