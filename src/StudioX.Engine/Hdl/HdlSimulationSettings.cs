namespace StudioX.Engine.Hdl;

/// <summary>事件仿真的输入与运行上限；仿真时间单位固定为 ns，VCD 保留工具的实际时间精度。</summary>
public sealed record HdlSimulationSettings(int FormatVersion, string[] Sources, string[] IncludeDirectories,
    string[] Defines, string TestbenchFile, string TestbenchTop, long DurationNanoseconds = 10000, int TimeoutSeconds = 30)
{
    public const string RelativePath = ".studiox/hdl-simulation.json";
    internal HdlSchematicSettings Inputs => new(1, Sources.Append(TestbenchFile).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        IncludeDirectories, Defines, TestbenchTop);
}
