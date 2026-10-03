namespace StudioX.Engine;

using StudioX.Packages;

/// <summary>
/// 已核实的 AGM MCU、封装和默认未压缩逻辑布局；容量来自厂商手册，不按料号猜测。
/// </summary>
public sealed record Ag32DeviceProfile(string DeviceId, string TargetDevice, string PackageName, int PinCount,
    uint FlashBytes, bool SupportsVerifiedDownload = false, uint ExternalPsramBytes = 0,
    string? SourceUrl = null, string? TargetSourceUrl = null)
{
    public uint FlashOrigin => 0x80000000;
    public uint RamOrigin => 0x20000000;
    public uint RamBytes => 0x20000;
    public uint MaximumSysClockMhz => 248;
    public bool CanMap => ApplicationFlashBytes is not null;
    public uint LogicReserveBytes => LogicImageBytes;
    public uint LogicImageBytes => 100 * 1024;
    public uint? ApplicationFlashBytes => FlashBytes > LogicImageBytes ? FlashBytes - LogicImageBytes : null;
    public uint? LogicImageAddress => ApplicationFlashBytes is { } size ? FlashOrigin + size : null;

    /// <summary>核对器件包的确定容量与锁定工具；不将相似型号或自定义布局当作匹配。</summary>
    public bool Matches(DeviceDefinition device)
    {
        return string.Equals(device.Id, DeviceId, StringComparison.OrdinalIgnoreCase) &&
            device.Architecture == "riscv" && device.ToolsetId == "agm.agrv" &&
            device.CompilerId == "agrv-gcc-11.1.0" &&
            device.FlashOrigin == FlashOrigin && device.FlashBytes == FlashBytes &&
            device.RamOrigin == RamOrigin && device.RamBytes == RamBytes;
    }
}
