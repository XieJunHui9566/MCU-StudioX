namespace StudioX.Application.Tools;

public sealed record ComponentMigrationResult(string SourceDirectory, string DestinationDirectory, bool Success, bool Cancelled,
    string Diagnostic, string? BuildLog)
{
    public string ToText() => (Success ? "验证副本编译成功。" : Cancelled ? "验证已取消；保留已创建副本。" : "验证副本未通过；保留源码与原始诊断。")
        + "\n副本：" + DestinationDirectory + "\n原工程：" + SourceDirectory + "\n" + Diagnostic
        + (BuildLog is null ? "" : "\n编译日志：" + BuildLog)
        + "\n原工程保持原版本，可继续打开使用。通过验证后请检查代码和板级行为，再决定采用副本。";
}
