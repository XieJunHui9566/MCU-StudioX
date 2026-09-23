namespace StudioX.Engine.Debugging;

using StudioX.Packages;

/// <summary>逐型号核对 WCH 目标的内存布局、厂商工具和调试配置。</summary>
public static class WchDebugTarget
{
    // 厂商 GDB 还枚举通用 RISC-V 的向量/虚拟化 CSR，并对未实现项返回 0。
    // 无 FPU 的 V203 不能读取厂商 GDB 泛列的浮点寄存器；编号仍从本次名称表解析。
    public static bool IsVisibleRegister(string name, bool hasFpu) => name is "zero" or "ra" or "sp" or "gp" or "tp" or "fp" or "pc" or
        "mstatus" or "misa" or "mtvec" or "mscratch" or "mepc" or "mcause" ||
        Numbered(name, "x", 31) || Numbered(name, "t", 6) || Numbered(name, "s", 11) || Numbered(name, "a", 7) ||
        hasFpu && (name is "fflags" or "frm" or "fcsr" ||
        Numbered(name, "f", 31) || Numbered(name, "ft", 11) || Numbered(name, "fs", 11) || Numbered(name, "fa", 7));

    private static bool Numbered(string name, string prefix, int maximum) => name.StartsWith(prefix, StringComparison.Ordinal) &&
        int.TryParse(name.AsSpan(prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number <= maximum;

    public static bool IsSupportedDevice(string id) => Layout(id) is not null;

    private static (int FlashKib, int ApplicationFlashKib, int RamKib, string Core, bool HasFpu)? Layout(string id) => id switch
    {
        "CH592D" or "CH592F" or "CH592X" => (448, 448, 26, "青稞 V4C · RV32", false),
        "CH595D" or "CH595F" or "CH595X" => (256, 240, 32, "青稞 V3C · RV32", false),
        "CH32V307VCT6" or "CH32V307RCT6" or "CH32V307WCU6" => (256, 256, 64, "青稞 V4F · RV32", true),
        "CH32V203C6T6" or "CH32V203F6P6" or "CH32V203G6U6" => (32, 32, 10, "青稞 V4B · RV32", false),
        "CH32V203C8T6" or "CH32V203C8U6" or "CH32V203F8P6" or "CH32V203F8U6" or
        "CH32V203G8R6" or "CH32V203K8T6" => (64, 64, 20, "青稞 V4B · RV32", false),
        "CH32V203RBT6" => (160, 160, 32, "青稞 V4B · RV32", false),
        "CH32V203CCT6" => (256, 256, 32, "青稞 · RV32IMCB", false),
        _ => null
    };

    public static DebugTargetProfile? Find(DeviceDefinition device)
    {
        if (Layout(device.Id) is not { } layout ||
            device.Architecture != "riscv" || device.ToolsetId != "wch.riscv" ||
            device.ToolsetVersion != "1.0.0" || device.CompilerId != "wch-gcc-12.2.0-v1.4" ||
            device.FlashOrigin != 0 || device.FlashBytes != layout.FlashKib * 1024 ||
            device.RamOrigin != 0x20000000 || device.RamBytes != layout.RamKib * 1024 ||
            device.OpenOcd is not { } config || config.ApplicationFlashBytes != layout.ApplicationFlashKib * 1024 ||
            config.TargetScript != "debug/" + device.Id.ToLowerInvariant() + ".cfg") return null;
        return new(device.Id, layout.Core, layout.HasFpu);
    }
}
