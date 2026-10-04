namespace StudioX.LabPlugins;

using System.Globalization;
using System.Text;
using System.Text.Json;
using StudioX.Extensions.Abstractions;
using static LabPanel;

public sealed class ProtocolLabPlugin : LabPlugin
{
    public override string Title => "协议校验助手";
    protected override string Introduction => "粘贴十六进制或 UTF-8 文本，观察字节、CRC 和校验和。计算输入中的全部字节；已有校验尾字节请先移除。";
    protected override JsonElement Schema => LabPanel.Schema(new()
    {
        ["encoding"] = EnumSchema("hex", "utf8"),
        ["data"] = StringSchema(4096)
    });
    protected override PluginPanelWidget[] Inputs(JsonElement values) =>
    [
        Select("encoding", "输入方式", LabInput.Text(values, "encoding", "hex"), ("hex", "HEX 字节"), ("utf8", "UTF-8 文本")),
        Input("data", "报文（最多 512 字节；HEX 可用空格、逗号和 0x 前缀）", LabInput.Text(values, "data", "01 03 00 00 00 0A"))
    ];

    public override LabResult Calculate(JsonElement values)
    {
        var text = LabInput.Text(values, "data", "01 03 00 00 00 0A");
        var bytes = LabInput.Text(values, "encoding", "hex") switch
        {
            "hex" => HexBytes(text),
            "utf8" => new UTF8Encoding(false, true).GetBytes(text),
            _ => throw new ArgumentException("请选择 HEX 或 UTF-8。")
        };
        if (bytes.Length is < 1 or > 512)
        {
            throw new ArgumentException("报文须包含 1–512 字节。");
        }
        uint modbus = 0xffff, ccitt = 0xffff, crc32 = 0xffffffff;
        var sum = 0;
        var xor = 0;
        // 参数和 123456789 检查值见 README 中 CRC RevEng 目录；此处为独立的逐位实现。
        foreach (var value in bytes)
        {
            sum = (sum + value) & 255;
            xor ^= value;
            modbus ^= value;
            crc32 ^= value;
            ccitt ^= (uint)value << 8;
            for (var bit = 0; bit < 8; bit++)
            {
                modbus = (modbus & 1) != 0 ? (modbus >> 1) ^ 0xa001u : modbus >> 1;
                crc32 = (crc32 & 1) != 0 ? (crc32 >> 1) ^ 0xedb88320u : crc32 >> 1;
                ccitt = ((ccitt & 0x8000) != 0 ? (ccitt << 1) ^ 0x1021u : ccitt << 1) & 0xffff;
            }
        }
        crc32 ^= 0xffffffff;
        var lrc = (-sum) & 255;
        var modbusTail = $"{modbus & 255:X2} {modbus >> 8:X2}";
        var normalized = string.Join(" ", bytes.Select(value => value.ToString("X2")));
        var rows = bytes.Chunk(8).Select((chunk, row) => new[]
        {
            (row * 8).ToString("X4"), string.Join(" ", chunk.Select(value => value.ToString("X2"))),
            new string(chunk.Select(value => value is >= 32 and <= 126 ? (char)value : '.').ToArray())
        });
        return new(new
        {
            byteCount = bytes.Length,
            modbus,
            ccittFalse = ccitt,
            crc32,
            sum8 = sum,
            xor8 = xor,
            lrc8 = lrc,
            modbusTail,
            normalized
        },
            $"/* {bytes.Length} bytes; CRC16/MODBUS tail (low byte first): {modbusTail} */\n#include <stdint.h>\nstatic const uint8_t packet[{bytes.Length}] = {{ {string.Join(", ", bytes.Select(value => $"0x{value:X2}"))} }};",
        [
            Table("checksums", "校验值", ["算法", "数值", "说明"],
            [
                ["CRC-16/MODBUS", $"0x{modbus:X4}", "尾字节（低字节先发）：" + modbusTail],
                ["CRC-16/IBM-3740", $"0x{ccitt:X4}", "别名 CCITT-FALSE；poly=1021 init=FFFF"],
                ["CRC-32/ISO-HDLC", $"0x{crc32:X8}", "poly=04C11DB7；反射；init/xorout=FFFFFFFF"],
                ["SUM8 / XOR8 / LRC8", $"{sum:X2} / {xor:X2} / {lrc:X2}", "LRC8 为累加和的二补数"]
            ]),
            new("distribution", "plot", "字节分布：X=字节偏移，Y=0–255", Json(bytes.Select((value, index) => new { x = index, y = value }))),
            Table("dump", "报文（ASCII 不可打印字符显示为点，最多展示前 480 字节）", ["偏移", "HEX", "ASCII"], rows)
        ]);
    }

    private static byte[] HexBytes(string text)
    {
        var tokens = text.Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        var result = new List<byte>();
        foreach (var item in tokens)
        {
            var token = item.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? item[2..] : item;
            if (token.Length == 0 || token.Length % 2 != 0)
            {
                throw new ArgumentException("HEX 每个字节需两位数字，不接受单个半字节。");
            }
            for (var index = 0; index < token.Length; index += 2)
            {
                if (!byte.TryParse(token.AsSpan(index, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
                {
                    throw new ArgumentException("HEX 含非法字符；仅接受 00–FF 字节及分隔符。");
                }
                result.Add(value);
                if (result.Count > 512)
                {
                    throw new ArgumentException("报文最多 512 字节。");
                }
            }
        }
        return result.ToArray();
    }
}
