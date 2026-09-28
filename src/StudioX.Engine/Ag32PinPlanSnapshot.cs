namespace StudioX.Engine;

/// <summary>图形编辑基于源文件散列；复杂 VE 保留原文并显示只读诊断。</summary>
public sealed record Ag32PinPlanSnapshot(string DeviceId, string TargetDevice, string SourcePath, string SourceSha256,
    Ag32PackagePin[] Pins, Ag32PinFunction[] Functions, Ag32PinAssignment[] Assignments,
    Ag32PinClockSettings Clocks, string[] Diagnostics, bool CanEdit);
