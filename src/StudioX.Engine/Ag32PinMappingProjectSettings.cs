namespace StudioX.Engine;

/// <summary>AG32 基础 MCU 引脚映射入口；开发环境组件版本与 MCU GCC、自定义 Verilog 综合分别锁定。</summary>
public sealed record Ag32PinMappingProjectSettings(string TargetDevice, string PinMapFile = "logic/pins.ve",
    string ToolsetId = "agm.pin-mapping", string ToolsetVersion = "1.0.0", string CompilerId = "agm.ve");
