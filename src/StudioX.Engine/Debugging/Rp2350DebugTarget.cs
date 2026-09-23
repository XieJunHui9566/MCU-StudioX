namespace StudioX.Engine.Debugging;

using StudioX.Packages;

/// <summary>外置 Flash 容量属于板型配置，不能从 RP2350 芯片名称推断。</summary>
public static class Rp2350DebugTarget
{
    public const string DeviceId = "RP2350A-PICO2";
    public static DebugTargetProfile? Find(DeviceDefinition device) =>
        device.Id == DeviceId && device.Architecture == "arm" &&
        device.ToolsetId == "arm.gnu" && device.ToolsetVersion == "1.0.0" && device.CompilerId == "arm-gnu-15.2.rel1" &&
        device.FlashOrigin == 0x10000000 && device.FlashBytes == 0x400000 &&
        device.RamOrigin == 0x20000000 && device.RamBytes == 520 * 1024 &&
        device.CpuFlags.Contains("-mcpu=cortex-m33") && device.CpuFlags.Contains("-mcmse") &&
        device.OpenOcd is { TargetScript: "debug/rp2350-pico2.cfg", ApplicationFlashBytes: 0x400000 }
            ? new(device.Id, "Cortex-M33 · RP2350", true) : null;
}
