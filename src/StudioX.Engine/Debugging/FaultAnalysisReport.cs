namespace StudioX.Engine.Debugging;

public sealed record FaultAnalysisReport(FaultEvidence Evidence, IReadOnlyList<string> Findings, IReadOnlyList<uint> Addresses,
    string SymbolInformation = "", string? ElfSha256 = null, CoreDumpEvidence? CoreDump = null)
{
    public string ToText() => $"器件：{Evidence.Device}\n来源：{Evidence.Source}\n宿主采集时间：{Evidence.CapturedAtUtc:O}\n固件匹配：{(CoreDump is { HashMatches: true } ? "已匹配转储内的 ELF 摘要（离线）" : Evidence.FirmwareMatched ? "已由当前调试会话校验" : "未确认；地址解析需匹配故障时的 ELF")}\nELF SHA-256：{ElfSha256 ?? "未选择"}\n\n"
        + (CoreDump is { } dump ? $"转储 SHA-256：{dump.DumpSha256}\n转储目标：{dump.Target} · IDF {dump.ToolsetVersion} · 解码器 {dump.DecoderVersion}\n转储 ELF 摘要：{dump.EmbeddedElfHash ?? "未提供"} · {(dump.HashMatches ? "已匹配" : "未确认")}\n符号来源：{dump.ElfSource}\n" + string.Join("\n", dump.Tasks.Select(t => $"任务 {t.Name} · TCB 0x{t.Tcb:x8} · 栈 {t.StackBytes} 字节 · flags={t.Flags}")) + "\n" : "")
        + string.Join("\n", Findings) + "\n\n" + (SymbolInformation == Evidence.Raw ? "" : SymbolInformation + "\n\n") + "原始现场：\n" + Evidence.Raw;
}
