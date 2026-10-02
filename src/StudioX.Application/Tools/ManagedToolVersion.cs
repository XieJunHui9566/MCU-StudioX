namespace StudioX.Application.Tools;

public sealed record ManagedToolVersion(string Id, string Version, string Name, string CompilerId, long Bytes, int Files,
    bool Installed, string? RetirementId, bool Latest, bool Busy, bool SafeToManage, string Fingerprint, string TreeStamp,
    IReadOnlyList<string> References, string Components, string Diagnostic, string RetirementState = "retired")
{
    public string SizeText => Bytes >= 1073741824 ? $"{Bytes / 1073741824d:F2} GiB" : $"{Bytes / 1048576d:F1} MiB";
    public string StateText => !Installed ? "可恢复区" : Busy ? "IDE 正在占用" : References.Count > 0 ? "依赖保留" : Latest ? "最新已安装" : "旧版本";
    public bool CanRetire => Installed && !Latest && !Busy && SafeToManage && References.Count == 0;
    public bool CanPurge => !Installed && !Busy && SafeToManage && References.Count == 0;
    public bool CanRestore => !Installed && RetirementId is not null && RetirementState == "retired";
    public string ReferenceText => References.Count == 0 ? "检查范围内未发现引用" : $"{References.Count} 项依赖";
}
