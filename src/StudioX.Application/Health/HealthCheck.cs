namespace StudioX.Application.Health;

public sealed record HealthCheck(string Code, string Title, HealthState State, string Detail,
    string HelpTopic, HealthAction Action = HealthAction.None, string RawDiagnostic = "",
    string? ToolsetId = null, string? ToolsetVersion = null)
{
    public string StateText => State switch { HealthState.Passed => "通过", HealthState.Warning => "注意", HealthState.Error => "需修复", _ => "说明" };
    public string SummaryDetail => Detail.Split('\n', 2)[0];
    public string ActionText => Action switch { HealthAction.Tools => "打开对应开发环境组件", HealthAction.BuildSettings => "打开编译设置",
        HealthAction.CMake => "打开 CMakeLists.txt", HealthAction.ResetCache => "预览并重建配置缓存", HealthAction.SdkSettings => "核对 ESP 模组与配置", _ => "阅读处理说明" };
}
