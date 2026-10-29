namespace StudioX.Engine.Pic;

using System.Text;
using StudioX.Foundation;

/// <summary>按 PIC16F877A DFP 的程序、配置和 EEPROM 地址检查 HEX；拒绝设备 ID 写入及隐式保护修改。</summary>
public static class PicHexImage
{
    public static PicHexImageInfo Validate(ReadOnlySpan<byte> image, bool backup = false)
    {
        if (image.Length == 0 || image.Length > 1024 * 1024)
        {
            throw Invalid("HEX 为空或超过大小限制。");
        }
        var bytes = new Dictionary<uint, byte>();
        uint upper = 0;
        var eof = false;
        foreach (var raw in Encoding.ASCII.GetString(image).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }
            if (eof || line[0] != ':' || line.Length < 11 || (line.Length & 1) == 0)
            {
                throw Invalid("HEX 记录或结束位置无效。");
            }
            byte[] record;
            try
            {
                record = Convert.FromHexString(line.AsSpan(1));
            }
            catch (FormatException) { throw Invalid("HEX 包含非十六进制字符。"); }
            var length = record[0];
            var address = (uint)(record[1] * 256 + record[2]);
            if (record.Length != length + 5 || (record.Sum(value => value) & 255) != 0)
            {
                throw Invalid("HEX 长度或校验和无效。");
            }
            switch (record[3])
            {
                case 0:
                    if (length == 0 || upper > 0x10000 || address + length > 0x10000)
                    {
                        throw Invalid("HEX 数据范围无效。");
                    }
                    for (uint index = 0; index < length; index++)
                    {
                        var target = upper + address + index;
                        var permitted = target < 0x4000 || target is 0x400e or 0x400f ||
                            backup && (target is >= 0x4000 and < 0x4008 or 0x400c or 0x400d or >= 0x4200 and < 0x4400);
                        if (!permitted || !bytes.TryAdd(target, record[4 + index]))
                        {
                            throw Invalid("HEX 含重叠、越界、设备 ID、用户 ID 或 EEPROM 写入；首阶段仅写程序与明确的配置字。");
                        }
                    }
                    break;
                case 1:
                    if (length != 0 || address != 0)
                    {
                        throw Invalid("HEX 结束记录无效。");
                    }
                    eof = true;
                    break;
                case 2 or 4:
                    if (length != 2 || address != 0)
                    {
                        throw Invalid("HEX 扩展地址记录无效。");
                    }
                    upper = (uint)(record[4] * 256 + record[5]) << (record[3] == 2 ? 4 : 16);
                    break;
                default:
                    throw Invalid("HEX 记录类型不受支持。");
            }
        }
        if (!eof || !bytes.ContainsKey(0) || !bytes.ContainsKey(0x400e) || !bytes.ContainsKey(0x400f))
        {
            throw Invalid("HEX 必须包含复位入口、完整配置字和结束记录；请在源码中明确设置配置位。");
        }
        foreach (var address in bytes.Keys)
        {
            if (!bytes.ContainsKey(address ^ 1))
            {
                throw Invalid("PIC HEX 含不完整的指令字。");
            }
            if ((address & 1) != 0 && bytes[address] > (address >= 0x4200 ? 0 : 0x3f))
            {
                throw Invalid("PIC 指令字或 EEPROM 数据超出器件位宽。");
            }
        }
        var config = (ushort)(bytes[0x400e] | bytes[0x400f] << 8);
        // DFP 1.9.176：CP 位 13、CPD 位 8。保护中的目标不能自动解除，新镜像也不能隐式开启保护。
        if ((config & 0x2100) != 0x2100)
        {
            throw new StudioXException("PIC_CODE_PROTECTED", "检测到程序或 EEPROM 代码保护；本入口不会自动解除或开启保护。");
        }
        return new(bytes.Keys.Count(address => address < 0x4000) / 2, config);
    }

    private static StudioXException Invalid(string message) => new("PIC_DOWNLOAD_IMAGE", message);
}
