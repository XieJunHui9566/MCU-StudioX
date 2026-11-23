namespace StudioX.Engine;

/// <summary>图形编辑基于源文件散列；复杂 VE 保留原文并显示只读诊断。</summary>
public sealed record Ag32PinPlanSnapshot(string DeviceId, string TargetDevice, string SourcePath, string SourceSha256,
    Ag32PackagePin[] Pins, Ag32PinFunction[] Functions, Ag32PinAssignment[] Assignments,
    Ag32PinClockSettings Clocks, string[] Diagnostics, bool CanEdit, Ag32AnalogSettings? Analog = null, Ag32UartSettings[]? Uarts = null)
{
    public string? VerilogSourcePath
    {
        get; init;
    }
    public string? VerilogSignature
    {
        get; init;
    }
    public Ag32VerilogPort[] VerilogPorts { get; init; } = [];
    public Ag32SystemPeripherals SystemPeripherals { get; init; } = new();
    public Ag32TimerSettings[] Timers { get; init; } = [];
    public Ag32BusSettings[] Buses { get; init; } = [];
}
