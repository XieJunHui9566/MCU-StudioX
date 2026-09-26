namespace StudioX.Engine;

using System.Buffers.Binary;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>按 ELF 的实际物理装载地址检查擦写范围，包括 RAM .data 的 Flash 装载映像。</summary>
internal static class FirmwareImage
{
    public static ulong Validate(byte[] bytes, string format, DeviceDefinition device)
    {
        if (device.OpenOcd?.ApplicationFlashBytes is { } applicationBytes)
        {
            if (applicationBytes == 0 || applicationBytes > device.FlashBytes) Fail("应用 Flash 范围配置无效。");
            device = device with { FlashBytes = applicationBytes };
        }
        if (format == "bin")
        {
            if (bytes.Length == 0 || bytes.Length > device.FlashBytes) Fail("固件大小超出器件应用 Flash 范围。");
            return (ulong)bytes.Length;
        }
        if (format != "elf" || bytes.Length < 52 || bytes[0] != 0x7f || bytes[1] != 'E' || bytes[2] != 'L' || bytes[3] != 'F' ||
            bytes[4] != 1 || bytes[5] != 1 || bytes[6] != 1 || U16(bytes, 16) != 2 ||
            U16(bytes, 18) != (device.Architecture == "arm" ? 40 : device.Architecture == "riscv" ? 243 : -1) || U32(bytes, 20) != 1)
            Fail("需要与器件架构匹配的 32 位小端 ELF 可执行固件。");
        var offset = U32(bytes, 28); var size = U16(bytes, 42); var count = U16(bytes, 44);
        if (size != 32 || count == 0 || (ulong)offset + (ulong)size * count > (ulong)bytes.Length) Fail("ELF 段表无效。");
        var ranges = new List<(ulong Start, ulong End)>();
        for (var index = 0; index < count; index++)
        {
            var row = checked((int)offset + index * size);
            if (U32(bytes, row) != 1) continue; // PT_LOAD；调试信息和 BSS 不写入 Flash。
            var fileOffset = U32(bytes, row + 4); var address = U32(bytes, row + 12);
            var length = U32(bytes, row + 16); var memoryLength = U32(bytes, row + 20);
            if (length == 0) continue;
            var end = (ulong)address + length;
            if (length > memoryLength || (ulong)fileOffset + length > (ulong)bytes.Length || address < device.FlashOrigin || end > (ulong)device.FlashOrigin + device.FlashBytes)
                Fail("ELF 装载段超出所选器件应用 Flash 范围，或固件文件损坏；请检查链接脚本。");
            if (ranges.Any(range => address < range.End && end > range.Start)) Fail("ELF 装载段相互重叠。");
            ranges.Add((address, end));
        }
        var entry = U32(bytes, 24) & ~1U;
        if (ranges.Count == 0 || !ranges.Any(range => entry >= range.Start && entry < range.End)) Fail("ELF 未包含有效的 Flash 程序入口。");
        return ranges.Aggregate(0UL, (sum, range) => checked(sum + range.End - range.Start));
    }
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static void Fail(string message) => throw new StudioXException("DOWNLOAD_IMAGE", message);
}
