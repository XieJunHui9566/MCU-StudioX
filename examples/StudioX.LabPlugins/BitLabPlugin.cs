namespace StudioX.LabPlugins;

using System.Globalization;
using System.Text.Json;
using StudioX.Extensions.Abstractions;
using static LabPanel;

public sealed class BitLabPlugin : LabPlugin
{
    public override string Title => "位运算实验室";
    protected override string Introduction => "把 8 / 16 / 32 位数拆开看：位域、掩码、有符号解释与大小端。仅计算数值，不读写真实寄存器。";
    protected override JsonElement Schema => LabPanel.Schema(new()
    {
        ["width"] = EnumSchema("8", "16", "32"), ["value"] = StringSchema(34),
        ["start"] = NumberSchema(0, 31, true), ["count"] = NumberSchema(1, 32, true),
        ["operand"] = StringSchema(34), ["operation"] = EnumSchema("inspect", "set", "clear", "toggle", "replace")
    });

    protected override PluginPanelWidget[] Inputs(JsonElement values) =>
    [
        Select("width", "位宽", LabInput.Text(values, "width", "32"), ("8", "8 位"), ("16", "16 位"), ("32", "32 位")),
        Input("value", "原始值（十进制 / 0x / 0b）", LabInput.Text(values, "value", "0x12345678")),
        Number("start", "位域起始位（最低位为 0）", LabInput.Integer(values, "start", 8, 0, 31)),
        Number("count", "位域长度", LabInput.Integer(values, "count", 8, 1, 32)),
        Select("operation", "操作", LabInput.Text(values, "operation", "inspect"), ("inspect", "查看位域"), ("set", "置 1"),
            ("clear", "清 0"), ("toggle", "翻转"), ("replace", "替换位域")),
        Input("operand", "替换值（仅“替换位域”使用）", LabInput.Text(values, "operand", "0xAB"))
    ];

    public override LabResult Calculate(JsonElement values)
    {
        var width = LabInput.Text(values, "width", "32") switch { "8" => 8, "16" => 16, "32" => 32, _ => throw new ArgumentException("位宽仅支持 8 / 16 / 32。") };
        var original = LabInput.Unsigned(LabInput.Text(values, "value", "0x12345678", 34));
        var start = LabInput.Integer(values, "start", 8, 0, 31);
        var count = LabInput.Integer(values, "count", 8, 1, 32);
        if (start + count > width) throw new ArgumentException("位域越过当前位宽；请同时调整起始位和长度。");
        var limit = (1UL << width) - 1;
        if (original > limit) throw new ArgumentException("原始值超出当前位宽，不会自动截断。");
        var fieldLimit = (1UL << count) - 1;
        var mask = fieldLimit << start;
        var operation = LabInput.Text(values, "operation", "inspect");
        var updated = operation switch
        {
            "inspect" => original, "set" => original | mask,
            "clear" => original & ~mask, "toggle" => original ^ mask,
            "replace" => Replace(), _ => throw new ArgumentException("未知位域操作。")
        };
        var signed = (updated & (1UL << (width - 1))) == 0 ? (long)updated : (long)updated - (1L << width);
        var hex = updated.ToString("X" + width / 4, CultureInfo.InvariantCulture);
        var maskHex = mask.ToString("X" + width / 4, CultureInfo.InvariantCulture);
        var binary = Convert.ToString((long)updated, 2).PadLeft(width, '0');
        var little = Enumerable.Range(0, width / 8).Select(index => ((updated >> (index * 8)) & 255).ToString("X2")).ToArray();
        var extracted = (updated & mask) >> start;
        var rows = Enumerable.Range(0, width).Reverse().Select(bit => new[]
            { bit.ToString(), ((original >> bit) & 1).ToString(), ((updated >> bit) & 1).ToString(), bit >= start && bit < start + count ? "选中" : "" });
        var code = $"#include <stdint.h>\n#define FIELD_MASK UINT32_C(0x{maskHex})\n#define FIELD_SHIFT {start}u\n/* 数值示例；确认寄存器语义后再使用。 */\nuint32_t value = UINT32_C(0x{hex});\nuint32_t field = (value & FIELD_MASK) >> FIELD_SHIFT;\n";
        return new(new { width, original, updated, mask, extracted, signed, binary, littleEndian = string.Join(" ", little), bigEndian = string.Join(" ", little.Reverse()) }, code,
        [
            Text("summary", "结果", $"0x{hex} · 无符号 {updated} · 有符号 {signed} · 位域值 {extracted}"),
            Text("binary", "二进制（高位在左）", string.Join(" ", Enumerable.Range(0, width / 4).Select(index => binary.Substring(index * 4, 4)))),
            Text("bytes", "内存字节（低地址 → 高地址）", "小端 " + string.Join(" ", little) + "；大端 " + string.Join(" ", little.Reverse())),
            Table("bits", "逐位观察", ["位", "原值", "结果", "位域"], rows)
        ]);
        ulong Replace()
        {
            var operand = LabInput.Unsigned(LabInput.Text(values, "operand", "0xAB", 34));
            if (operand > fieldLimit) throw new ArgumentException("替换值超出位域范围，不会自动截断。");
            return (original & ~mask) | (operand << start);
        }
    }
}
