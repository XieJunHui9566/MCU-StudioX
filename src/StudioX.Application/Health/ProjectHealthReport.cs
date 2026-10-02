namespace StudioX.Application.Health;

using System.Text;

public sealed record ProjectHealthReport(string? ProjectDirectory, string ProjectName, string Target,
    DateTimeOffset CheckedUtc, bool DeepVerification, bool NativeBuildApplicable, IReadOnlyList<HealthCheck> Checks)
{
    public int Errors => Checks.Count(check => check.State == HealthState.Error);
    public int Warnings => Checks.Count(check => check.State == HealthState.Warning);
    public bool CanBuild => NativeBuildApplicable && Errors == 0;
    public string Summary => $"{Errors} 项需修复 · {Warnings} 项注意 · {Checks.Count} 项检查 · " + (DeepVerification ? "完整工具校验与启动检查" : "快速配置检查，工具内容未做完整哈希校验");
    public string ToText()
    {
        var text = new StringBuilder().AppendLine("MCU StudioX 工程健康检查").AppendLine("工程：" + ProjectName)
            .AppendLine("目录：" + (ProjectDirectory ?? "未选择工程")).AppendLine("目标：" + Target)
            .AppendLine("检查时间（UTC）：" + CheckedUtc.ToString("O")).AppendLine(Summary)
            .AppendLine("检查不连接设备、不修改源码、SDK、sdkconfig 或工程锁定。通过不代表固件编译或实板验收。\n");
        foreach (var check in Checks)
        {
            text.AppendLine($"[{check.StateText}] {check.Title} ({check.Code})").AppendLine(check.Detail)
                .AppendLine("处理入口：" + check.ActionText + "；帮助主题：" + check.HelpTopic);
            if (check.RawDiagnostic.Length > 0) text.AppendLine("原始诊断：").AppendLine(check.RawDiagnostic);
            text.AppendLine();
        }
        return text.ToString();
    }
}
