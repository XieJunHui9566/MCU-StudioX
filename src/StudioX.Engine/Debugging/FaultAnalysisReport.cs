namespace StudioX.Engine.Debugging;

public sealed record FaultAnalysisReport(FaultEvidence Evidence, IReadOnlyList<string> Findings, IReadOnlyList<uint> Addresses,
    string SymbolInformation = "", string? ElfSha256 = null)
{
    public string ToText() => $"器件：{Evidence.Device}\n来源：{Evidence.Source}\n宿主采集时间：{Evidence.CapturedAtUtc:O}\n固件匹配：{(Evidence.FirmwareMatched ? "已由当前调试会话校验" : "未确认；地址解析需匹配故障时的 ELF")}\nELF SHA-256：{ElfSha256 ?? "未选择"}\n\n"
        + string.Join("\n", Findings) + "\n\n" + SymbolInformation + "\n\n原始现场：\n" + Evidence.Raw;
}
