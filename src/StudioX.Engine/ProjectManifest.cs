namespace StudioX.Engine;

public sealed record ProjectManifest(int FormatVersion, string Name, string PackId, string PackVersion, string PackContentHash,
    string DeviceId, string TemplateId, string ToolsetId, string ToolsetVersion, string CompilerId,
    ProjectKind Kind = ProjectKind.Pack, CubeMxProjectSettings? CubeMx = null, Ag32LogicProjectSettings? Logic = null);
public enum ProjectKind { Pack, CubeMx }
public sealed record CubeMxProjectSettings(string IocFile, string ToolchainFile, string? ConfigurePreset, string BuildType = "Debug");
/// <summary>AG32 逻辑工程的独立入口；与 MCU 固件构建、下载目标分开。</summary>
public sealed record Ag32LogicProjectSettings(string TargetDevice, string VerilogFile, string PinMapFile);
public sealed record ToolchainLock(int FormatVersion, string ToolsetId, string ToolsetVersion, string Fingerprint);
public sealed record BuildPlan(ProjectManifest Project, StudioX.Packages.DeviceDefinition Device);
public sealed record BuildReport(bool Success, string Log, IReadOnlyList<string> Artifacts, string? LogPath = null, int? ExitCode = null, bool TimedOut = false)
{
    public string Summary => $"{(Success ? "编译成功" : "编译失败")}，退出代码：{(ExitCode is { } code ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "不可用")}{(TimedOut ? "（工具执行超时）" : "")}";
}
