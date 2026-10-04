namespace StudioX.Application.Tools;

/// <summary>未完成事务的只读恢复预览；路径由组件身份和事务编号推导，不能由调用方指定。</summary>
public sealed record ToolRepairRecovery(string TransactionId, string Id, string Version, string CompilerId,
    string RecordHash, string InputStamp, string Action, string Detail)
{
    public string Label => Id + " / " + Version + " · " + ActionText;
    public bool CanRestorePrevious
    {
        get; init;
    }
    public bool CanRecover => Action != "blocked";
    public string ActionText => Action switch
    {
        "finish" => "继续完成安装或修复",
        "restore" => "恢复原组件",
        "confirm" => "校验已就位组件并完成记录",
        "keep" => "保留现有组件并结束事务",
        _ => "查看恢复诊断"
    };
}
