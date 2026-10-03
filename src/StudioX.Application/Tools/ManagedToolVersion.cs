namespace StudioX.Application.Tools;

public sealed record ManagedToolVersion(string Id, string Version, string Name, string CompilerId, long Bytes, int Files,
    bool Installed, string? RetirementId, bool Latest, bool Busy, bool SafeToManage, string Fingerprint, string TreeStamp,
    IReadOnlyList<string> References, string Components, string Diagnostic, string RetirementState = "retired", bool Enabled = true)
{
    public string SizeText => Bytes >= 1073741824 ? $"{Bytes / 1073741824d:F2} GiB" : $"{Bytes / 1048576d:F1} MiB";
    public string StateText => !Installed ? "可恢复区" : Busy ? "IDE 正在占用" : !Enabled ? "已禁用" : References.Count > 0 ? "依赖保留" : Latest ? "最新已安装" : "旧版本";
    public bool CanToggle => Installed && !Busy && SafeToManage;
    public bool CanRemove => Installed && !Busy && SafeToManage;
    public bool CanDelete => !Busy && SafeToManage && (Installed || RetirementId is not null);
    public string ManagementHint => Busy ? "正在使用此组件，请先结束构建、调试或预览，再刷新列表。"
        : !SafeToManage ? "组件身份或依赖检查未完成，请查看诊断并解决后再管理。"
        : !Installed ? "可恢复区仍占磁盘空间；恢复后可继续使用，永久删除后需重新导入。"
        : References.Count > 0 ? $"此版本被 {References.Count} 项工程、器件包或功能引用。禁用或删除后，相应入口会提示缺少组件；工程锁定保留。"
        : "禁用保留文件，可随时启用；删除释放空间，也可先移至可恢复区。";
    public bool CanRetire => Installed && !Latest && !Busy && SafeToManage && References.Count == 0;
    public bool CanPurge => !Installed && !Busy && SafeToManage && References.Count == 0;
    public bool CanRestore => !Installed && RetirementId is not null && RetirementState == "retired";
    public string ReferenceText => References.Count == 0 ? "检查范围内未发现引用" : $"{References.Count} 项依赖";
}
