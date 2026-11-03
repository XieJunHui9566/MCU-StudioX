namespace StudioX.Engine.Pic;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using StudioX.Foundation;

/// <summary>地址、配置和保护位从准确型号 EDC 读取，不把物理 RAM 容量当作连续寻址范围。</summary>
public sealed class PicDebugMemoryMap
{
    private readonly XDocument document;
    private readonly (uint Start, uint End)[] code, config;
    public uint FileRegisterEnd
    {
        get;
    }
    public uint ProgramWordEnd
    {
        get;
    }
    public PicDebugMemoryMap(string edcFile, string deviceId)
    {
        using var reader = XmlReader.Create(edcFile, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        document = XDocument.Load(reader);
        if (Attribute(document.Root!, "name") != deviceId)
        {
            throw new StudioXException("PIC_DEBUG_DEVICE", "EDC 器件身份不一致。");
        }
        code = Ranges("CodeSector");
        config = Ranges("ConfigFuseSector");
        ProgramWordEnd = code.Max(r => r.End);
        FileRegisterEnd = Ranges("GPRDataSector").Concat(Ranges("SFRDataSector")).Max(r => r.End);
    }
    private (uint Start, uint End)[] Ranges(string name) => document.Descendants().Where(e => e.Name.LocalName == name)
        .Select(e => (Number(Attribute(e, "beginaddr")), Number(Attribute(e, "endaddr")))).ToArray();
    private static string Attribute(XElement element, string name) => element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value
        ?? throw new StudioXException("PIC_EDC", "EDC 缺少属性：" + name);
    private static uint Number(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? uint.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : uint.Parse(text, CultureInfo.InvariantCulture);

    public Dictionary<uint, byte> Validate(byte[] image, bool backup)
    {
        if (image.Length is 0 or > 1024 * 1024)
        {
            throw Invalid("HEX 大小无效。");
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
            if (eof || line[0] != ':' || line.Length < 11 || line.Length % 2 == 0)
            {
                throw Invalid("HEX 记录格式无效。");
            }
            byte[] record;
            try
            {
                record = Convert.FromHexString(line.AsSpan(1));
            }
            catch (FormatException ex) { throw new StudioXException("PIC_DEBUG_HEX", "HEX 包含非十六进制字符。", ex); }
            if (record.Length != record[0] + 5 || record.Sum(b => b) % 256 != 0)
            {
                throw Invalid("HEX 校验和或长度无效。");
            }
            var address = (uint)(record[1] * 256 + record[2]);
            switch (record[3])
            {
                case 0:
                    if (record[0] == 0 || address + record[0] > 0x10000)
                    {
                        throw Invalid("HEX 数据范围无效。");
                    }
                    for (uint i = 0; i < record[0]; i++)
                    {
                        var target = checked(upper + address + i);
                        if ((!backup && !code.Concat(config).Any(r => target >= r.Start * 2 && target < r.End * 2)) || !bytes.TryAdd(target, record[4 + i]))
                        {
                            throw Invalid("调试 HEX 只能包含该 EDC 的程序与配置区域，不允许 EEPROM、用户 ID 或重叠记录。");
                        }
                    }
                    break;
                case 1:
                    if (record[0] != 0 || address != 0)
                    {
                        throw Invalid("HEX 结束记录无效。");
                    }
                    eof = true;
                    break;
                case 2 or 4:
                    if (record[0] != 2 || address != 0)
                    {
                        throw Invalid("HEX 扩展地址记录无效。");
                    }
                    upper = (uint)(record[4] * 256 + record[5]) << (record[3] == 2 ? 4 : 16);
                    break;
                default:
                    throw Invalid("不支持的 PIC HEX 记录类型。");
            }
        }
        if (!eof || !bytes.ContainsKey(0) || !bytes.ContainsKey(1))
        {
            throw Invalid("HEX 缺少程序复位入口或结束记录。");
        }
        foreach (var definition in document.Descendants().Where(e => e.Name.LocalName == "DCRDef"))
        {
            var address = Number(Attribute(definition, "_addr")) * 2;
            if (!bytes.TryGetValue(address, out var lo) || !bytes.TryGetValue(address + 1, out var hi))
            {
                throw Invalid("HEX 缺少准确型号的完整配置字。");
            }
            var word = (uint)(lo | hi << 8);
            foreach (var mode in definition.Descendants().Where(e => e.Name.LocalName == "DCRMode"))
            {
                var offset = 0;
                foreach (var field in mode.Elements())
                {
                    if (field.Name.LocalName == "AdjustPoint")
                    {
                        offset += checked((int)Number(Attribute(field, "offset")));
                        continue;
                    }
                    if (field.Name.LocalName != "DCRFieldDef")
                    {
                        continue;
                    }
                    var name = Attribute(field, "name");
                    if (name.StartsWith("CP", StringComparison.Ordinal) || name == "WRT")
                    {
                        var off = field.Elements().FirstOrDefault(e => e.Name.LocalName == "DCRFieldSemantic" && Attribute(e, "cname") == "OFF");
                        var match = off is null ? Match.Empty : Regex.Match(Attribute(off, "when"), @"\(field\s*&\s*(0x[0-9a-fA-F]+)\)\s*==\s*(0x[0-9a-fA-F]+)");
                        if (!match.Success || ((word >> offset) & Number(match.Groups[1].Value)) != Number(match.Groups[2].Value))
                        {
                            throw new StudioXException("PIC_CODE_PROTECTED", "代码或写保护未关闭：" + name + "；调试不会自动解除保护。");
                        }
                    }
                    offset += checked((int)Number(Attribute(field, "nzwidth")));
                }
            }
        }
        return bytes;
    }
    public void RequireDebugConfiguration(byte[] image, string deviceId)
    {
        var bytes = Validate(image, false);
        if (deviceId == "PIC16F877A" && ((bytes[0x400e] | bytes[0x400f] << 8) & 0x0008) == 0)
        {
            // 6.20 实板后端要求 PWRT disabled；不静默覆盖用户普通构建的配置。
            throw new StudioXException("PIC_DEBUG_CONFIG", "PIC16F877A 调试要求 PWRTE = OFF。请在 STUDIOX_PIC_DEBUG 条件下设置调试配置，普通构建可保留 PWRTE = ON；未连接或写入目标。");
        }
    }
    private static StudioXException Invalid(string message) => new("PIC_DEBUG_HEX", message);
}
