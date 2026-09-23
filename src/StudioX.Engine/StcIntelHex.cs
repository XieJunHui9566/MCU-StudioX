namespace StudioX.Engine;

using StudioX.Foundation;

internal sealed record StcIntelHexInfo(int DataBytes, int HighestAddress);

/// <summary>只接受 STC 8 位工程的 0 基址 Intel HEX；防止 stcgal 将越界数据写入 EEPROM 或静默截断。</summary>
internal static class StcIntelHex
{
    internal static StcIntelHexInfo Validate(ReadOnlySpan<byte> image, uint usableFlashBytes)
    {
        if (image.Length == 0 || image.Length > 1024 * 1024 || usableFlashBytes == 0 || usableFlashBytes > 0x10000)
            throw Invalid("固件为空、过大或器件 Flash 范围无效。");
        var text = System.Text.Encoding.ASCII.GetString(image);
        var max = -1;
        var count = 0;
        var covered = new bool[checked((int)usableFlashBytes)];
        var eof = false;
        var firstByte = false;
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            lineNumber++;
            if (eof || line[0] != ':' || line.Length < 11 || (line.Length & 1) == 0)
                throw Invalid($"Intel HEX 第 {lineNumber} 行格式或结束记录无效。");
            byte[] record;
            try { record = Convert.FromHexString(line.AsSpan(1)); }
            catch (FormatException) { throw Invalid($"Intel HEX 第 {lineNumber} 行含非十六进制字符。"); }
            var length = record[0];
            if (record.Length != length + 5 || (record.Sum(value => value) & 0xff) != 0)
                throw Invalid($"Intel HEX 第 {lineNumber} 行长度或校验和错误。");
            var address = record[1] * 256 + record[2];
            var type = record[3];
            switch (type)
            {
                case 0:
                    if (length == 0 || (ulong)address + length > usableFlashBytes)
                        throw Invalid("程序数据越过所选型号的代码 Flash 上限，或数据记录为空。");
                    // SDCC 会把中断向量与函数体按非地址顺序输出；但重叠记录有多种解释，必须拒绝。
                    for (var offset = address; offset < address + length; offset++)
                    {
                        if (covered[offset]) throw Invalid("Intel HEX 代码记录相互重叠，下载结果不确定。");
                        covered[offset] = true;
                    }
                    count += length;
                    max = Math.Max(max, address + length - 1);
                    if (address == 0) firstByte = true;
                    break;
                case 1:
                    if (length != 0 || address != 0) throw Invalid("Intel HEX 结束记录无效。");
                    eof = true;
                    break;
                case 2 or 4:
                    if (length != 2 || address != 0 || record[4] != 0 || record[5] != 0)
                        throw Invalid("STC 8 位固件只允许 0 基址代码空间，不接受扩展地址或 EEPROM 段。");
                    break;
                case 3 or 5:
                    throw Invalid("STC 8 位固件不接受扩展程序入口记录。");
                default:
                    throw Invalid($"Intel HEX 记录类型 {type} 不受支持。");
            }
        }
        if (!eof || count == 0 || !firstByte) throw Invalid("固件缺少结束记录、代码数据或 0x0000 程序入口。");
        return new(count, max);
    }

    private static StudioXException Invalid(string detail) => new("STC_ISP_IMAGE", detail);
}
