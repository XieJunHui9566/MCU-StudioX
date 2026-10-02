namespace StudioX.Engine.Svd;

using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using StudioX.Foundation;

/// <summary>仅解析有界 XML 数据；不解析实体、不联网。未知访问属性保持禁用，不能猜测为可读写。</summary>
public sealed class SvdParser
{
    private readonly Dictionary<string, XElement> index = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> resolved = new(StringComparer.Ordinal);
    private readonly HashSet<string> resolving = new(StringComparer.Ordinal);
    private readonly List<SvdRegister> registers = [];
    private static string? Text(XElement node, string name) => node.Element(name)?.Value.Trim();
    private static string Required(XElement node, string name) => Text(node, name) ?? throw Error("缺少 " + name);
    private static StudioXException Error(string text) => new("SVD_FORMAT", text);

    public static async Task<SvdDevice> LoadAsync(string file, CancellationToken token = default)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > 32 * 1024 * 1024) { throw Error("SVD 大小必须为 1 字节到 32 MiB。"); }
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token);
        return await Task.Run(() => Parse(bytes), token);
    }
    public static SvdDevice Parse(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 32 * 1024 * 1024) { throw Error("SVD 超出大小限制。"); }
        using var reader = XmlReader.Create(new MemoryStream(bytes), new() { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024, MaxCharactersFromEntities = 0 });
        var root = XDocument.Load(reader).Root ?? throw Error("空 XML。");
        if (root.Name != "device" || root.Descendants().Take(250001).Count() > 250000) { throw Error("不是受支持的 SVD device 或节点过多。"); }
        if (root.Descendants().Any(e => e.Ancestors().Take(33).Count() > 32)) { throw Error("SVD 嵌套过深。"); }
        if (root.Descendants("enumeratedValues").Any(e => e.Attribute("derivedFrom") is not null)) { throw Error("暂不支持枚举分组 derivedFrom；请使用展开定义。"); }
        if (Number(Text(root, "addressUnitBits") ?? "8") != 8) { throw Error("仅支持 8 位地址单元。"); }
        var parser = new SvdParser();
        var peripherals = root.Element("peripherals") ?? throw Error("缺少 peripherals。");
        foreach (var p in peripherals.Elements("peripheral")) { parser.Index(p, ""); }
        var defaults = new Properties(32, "unknown", null, null, null);
        defaults = defaults.Inherit(root);
        foreach (var p in peripherals.Elements("peripheral"))
        {
            var name = Required(p, "name");
            var effective = parser.Resolve(name);
            parser.IndexInheritedChildren(effective, name);
            foreach (var (instance, delta) in Instances(effective))
            {
                var address = checked(Number(Required(effective, "baseAddress")) + delta);
                parser.Walk(effective.Element("registers")?.Elements() ?? [], address, instance, instance, defaults.Inherit(effective), name);
            }
        }
        if (parser.registers.Count == 0 || parser.registers.Select(r => r.Path).Distinct().Count() != parser.registers.Count) { throw Error("无寄存器或寄存器名称重复。"); }
        return new(Required(root, "name"), Text(root, "description") ?? "", Convert.ToHexString(SHA256.HashData(bytes)), parser.registers.ToArray());
    }
    private void Index(XElement element, string parent)
    {
        var name = Required(element, "name");
        var path = parent.Length == 0 ? name : parent + "." + name;
        if (!index.TryAdd(path, element)) { throw Error("重复定义：" + path); }
        foreach (var child in element.Elements().Where(e => e.Name is { LocalName: "register" or "cluster" or "field" })) { Index(child, path); }
        foreach (var container in element.Elements().Where(e => e.Name is { LocalName: "registers" or "fields" }))
        { foreach (var child in container.Elements()) { Index(child, path); } }
    }
    private XElement Resolve(string path)
    {
        if (resolved.TryGetValue(path, out var value)) { return value; }
        if (!index.TryGetValue(path, out var node)) { throw Error("derivedFrom 引用不存在：" + path); }
        if (!resolving.Add(path)) { throw Error("derivedFrom 循环：" + path); }
        try
        {
            var derived = node.Attribute("derivedFrom")?.Value;
            var result = new XElement(node);
            if (derived is not null)
            {
                var parent = path.Contains('.') ? path[..path.LastIndexOf('.')] + "." : "";
                var reference = derived.Contains('.') ? derived : parent + derived;
                var basis = Resolve(reference);
                if (basis.Name != node.Name) { throw Error("derivedFrom 类型不一致：" + path); }
                foreach (var child in basis.Elements())
                { if (result.Element(child.Name) is null) { result.Add(new XElement(child)); } }
            }
            result.Attribute("derivedFrom")?.Remove();
            resolved[path] = result;
            return result;
        }
        finally { resolving.Remove(path); }
    }
    private void Walk(IEnumerable<XElement> elements, ulong basis, string prefix, string peripheral, Properties inherited, string definitionPrefix)
    {
        foreach (var raw in elements)
        {
            if (raw.Name.LocalName is not ("register" or "cluster")) { throw Error("未知寄存器分组。"); }
            var definitionPath = definitionPrefix + "." + Required(raw, "name");
            // 继承外设的子项仍可能来自原始外设；用该元素所属的真实定义解析其引用。
            var original = index.ContainsKey(definitionPath) ? Resolve(definitionPath) : ResolveInherited(raw, definitionPrefix);
            var props = inherited.Inherit(original);
            foreach (var (name, delta) in Instances(original))
            {
                var address = checked(basis + Number(Required(original, "addressOffset")) + delta);
                var path = prefix + "." + name;
                if (raw.Name == "cluster") { Walk(original.Elements().Where(e => e.Name.LocalName is "register" or "cluster"), address, path, peripheral, props, definitionPath); continue; }
                if (props.Width is not (8 or 16 or 32 or 64) || address + (uint)(props.Width / 8) > (ulong)uint.MaxValue + 1 || address % (uint)(props.Width / 8) != 0)
                { throw Error("寄存器位宽、对齐或地址不支持：" + path); }
                var fields = new List<SvdField>();
                foreach (var fieldRaw in original.Element("fields")?.Elements("field") ?? [])
                {
                    var fieldPath = definitionPath + "." + Required(fieldRaw, "name");
                    var field = index.ContainsKey(fieldPath) ? Resolve(fieldPath) : ResolveInherited(fieldRaw, definitionPath);
                    var (offset, width) = Bits(field);
                    foreach (var (fieldName, bitDelta) in Instances(field))
                    {
                        if (++expandedItems > 100000) { throw Error("展开定义过多。"); }
                        var bitOffset = checked(offset + (int)bitDelta);
                        if (width < 1 || bitOffset < 0 || bitOffset + width > props.Width) { throw Error("位域越界：" + fieldPath); }
                        var enums = new Dictionary<ulong, string>();
                        foreach (var e in field.Elements("enumeratedValues").Elements("enumeratedValue"))
                        {
                            if (++expandedItems > 100000) { throw Error("展开枚举过多。"); }
                            var v = Text(e, "value");
                            if (v is not null && !v.Contains('x', StringComparison.OrdinalIgnoreCase) || v?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true)
                            { if (v is not null) { enums[Number(v)] = Required(e, "name") + " " + (Text(e, "description") ?? ""); } }
                        }
                        fields.Add(new(fieldName, bitOffset, width, Text(field, "access") ?? props.Access, Text(field, "description") ?? "",
                            Text(field, "readAction"), Text(field, "modifiedWriteValues"), enums));
                    }
                }
                if (++registerCount > 20000) { throw Error("展开后寄存器超过 20,000 个。"); }
                registers.Add(new(path, peripheral, (uint)address, props.Width, props.Access, Text(original, "description") ?? "", props.Reset,
                    props.ReadAction, props.WriteValues, original.Element("writeConstraint") is not null || original.Descendants("writeConstraint").Any(), fields));
            }
        }
    }
    private int registerCount;
    private int expandedItems;
    private void IndexInheritedChildren(XElement element, string path)
    {
        var children = element.Elements().Where(e => e.Name.LocalName is "register" or "cluster" or "field")
            .Concat(element.Elements().Where(e => e.Name.LocalName is "registers" or "fields").Elements());
        foreach (var child in children)
        {
            var key = path + "." + Required(child, "name");
            index.TryAdd(key, child);
            IndexInheritedChildren(Resolve(key), key);
        }
    }
    private XElement ResolveInherited(XElement raw, string parent)
    {
        if (raw.Attribute("derivedFrom") is not { } attr) { return raw; }
        var reference = attr.Value.Contains('.') ? attr.Value : parent + "." + attr.Value;
        var basis = Resolve(reference);
        var result = new XElement(raw);
        foreach (var child in basis.Elements()) { if (result.Element(child.Name) is null) { result.Add(new XElement(child)); } }
        return result;
    }
    private sealed record Properties(int Width, string Access, ulong? Reset, string? ReadAction, string? WriteValues)
    {
        public Properties Inherit(XElement e) => new(checked((int)Number(Text(e, "size") ?? Width.ToString(CultureInfo.InvariantCulture))),
            Text(e, "access") ?? Access, Text(e, "resetValue") is { } reset ? Number(reset) : Reset,
            Text(e, "readAction") ?? ReadAction, Text(e, "modifiedWriteValues") ?? WriteValues);
    }
    private static (int Offset, int Width) Bits(XElement e)
    {
        if (Text(e, "bitOffset") is { } offset) { return (checked((int)Number(offset)), checked((int)Number(Required(e, "bitWidth")))); }
        if (Text(e, "lsb") is { } lsb) { var lo = checked((int)Number(lsb)); return (lo, checked((int)Number(Required(e, "msb"))) - lo + 1); }
        var range = Required(e, "bitRange").Trim('[', ']').Split(':');
        if (range.Length != 2) { throw Error("位域范围无效。"); }
        var low = checked((int)Number(range[1])); return (low, checked((int)Number(range[0])) - low + 1);
    }
    private static IEnumerable<(string Name, ulong Delta)> Instances(XElement e)
    {
        var name = Required(e, "name");
        var count = checked((int)Number(Text(e, "dim") ?? "1"));
        if (count is < 1 or > 1024) { throw Error("数组大小无效。"); }
        var increment = count == 1 && Text(e, "dim") is null ? 0 : Number(Required(e, "dimIncrement"));
        if (count > 1 && (increment == 0 || !name.Contains("%s", StringComparison.Ordinal))) { throw Error("数组缺少占位符或增量。"); }
        string[] indexes;
        var text = Text(e, "dimIndex");
        if (text is null) { indexes = Enumerable.Range(0, count).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray(); }
        else if (text.Contains(',')) { indexes = text.Split(',').Select(s => s.Trim()).ToArray(); }
        else if (text.Contains('-'))
        {
            var parts = text.Split('-');
            if (parts.Length != 2) { throw Error("dimIndex 无效。"); }
            if (int.TryParse(parts[0], out var start) && int.TryParse(parts[1], out var end) && end - start + 1 == count) { indexes = Enumerable.Range(start, count).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray(); }
            else if (parts[0].Length == 1 && parts[1].Length == 1 && parts[1][0] - parts[0][0] + 1 == count) { indexes = Enumerable.Range(parts[0][0], count).Select(i => ((char)i).ToString()).ToArray(); }
            else { throw Error("dimIndex 范围无效。"); }
        }
        else { indexes = [text]; }
        if (indexes.Length != count) { throw Error("dimIndex 数量不匹配。"); }
        for (var i = 0; i < count; i++) { yield return (name.Replace("%s", indexes[i], StringComparison.Ordinal), checked((ulong)i * increment)); }
    }
    private static ulong Number(string text)
    {
        text = text.Trim();
        var multiplier = text.EndsWith('K') ? 1024UL : text.EndsWith('M') ? 1024UL * 1024 : text.EndsWith('G') ? 1024UL * 1024 * 1024 : 1;
        if (multiplier != 1) { text = text[..^1]; }
        try { return checked((text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt64(text[2..], 16) : text.StartsWith('#') ? Convert.ToUInt64(text[1..], 2) : ulong.Parse(text, CultureInfo.InvariantCulture)) * multiplier); }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException) { throw Error("数值无效：" + text); }
    }
}
