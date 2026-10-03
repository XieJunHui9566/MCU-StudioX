namespace StudioX.Engine;

/// <summary>AG32 逻辑工程的独立入口；与 MCU 固件构建、下载目标分开。</summary>
public sealed record Ag32LogicProjectSettings(string TargetDevice, string VerilogFile, string PinMapFile,
    string ToolsetId = "agm.logic", string ToolsetVersion = "1.0.0", string CompilerId = "agm.native");
