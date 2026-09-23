namespace StudioX.Engine.Debugging;

using StudioX.Packages;

public sealed record DebugTargetProfile(string DeviceId, string Core, bool HasFpu)
{
    public bool IsAg32 => DeviceId == "AG32VF303CCT6";
    public bool IsWch => WchDebugTarget.IsSupportedDevice(DeviceId);
    public bool IsRp2350 => DeviceId == Rp2350DebugTarget.DeviceId;
    public string RegisterTitle => Core + (HasFpu ? " · 内核 / FPU" : " · 内核寄存器");
    public static DebugTargetProfile? Find(DeviceDefinition device)
    {
        if (device.Id == "AG32VF303CCT6" && device.Architecture == "riscv" &&
            device.ToolsetId == "agm.agrv" && device.ToolsetVersion == "1.0.0" && device.CompilerId == "agrv-gcc-11.1.0" &&
            device.FlashOrigin == 0x80000000 && device.FlashBytes == 0x40000 &&
            device.RamOrigin == 0x20000000 && device.RamBytes == 0x20000 &&
            device.OpenOcd is { ApplicationFlashBytes: 0x27000, TargetScript: "debug/ag32vf303.cfg" })
            return new(device.Id, "AgRV · RV32", true);
        return Rp2350DebugTarget.Find(device) ?? WchDebugTarget.Find(device) ?? Stm32DebugTarget.Find(device);
    }
}
