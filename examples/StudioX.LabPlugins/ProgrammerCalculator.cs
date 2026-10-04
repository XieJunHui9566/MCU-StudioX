namespace StudioX.LabPlugins;

using System.Globalization;
using System.Text.Json;
using static LabPanel;

internal static class ProgrammerCalculator
{
    internal static LabResult Calculate(JsonElement values)
    {
        if (values.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("计算参数必须是对象。");
        }
        var width = LabInput.Text(values, "width", "32") switch
        {
            "8" => 8,
            "16" => 16,
            "32" => 32,
            "64" => 64,
            _ => throw new ArgumentException("位宽仅支持 8 / 16 / 32 / 64。")
        };
        var radix = LabInput.Text(values, "radix", "10") switch
        {
            "2" => 2,
            "8" => 8,
            "10" => 10,
            "16" => 16,
            _ => throw new ArgumentException("进制仅支持 BIN / OCT / DEC / HEX。")
        };
        var word = new ProgrammerWord(width, radix, LabInput.Boolean(values, "signedMode"));
        var operation = LabInput.Text(values, "operation", values.TryGetProperty("start", out _) || values.TryGetProperty("count", out _) ? "inspect" : "evaluate");
        var field = operation is "inspect" or "set" or "clear" or "toggle" or "replace";
        var value = LabInput.Text(values, "value", "0x12345678", 1024).Trim();
        var original = field ? word.Parse(value) : new ProgrammerExpression(word, value).Evaluate();
        var updated = original;
        var start = field ? LabInput.Integer(values, "start", Math.Min(8, width - 1), 0, 63) : 0;
        var count = field ? LabInput.Integer(values, "count", Math.Min(8, width - start), 1, 64) : width;
        if (start + count > width)
        {
            throw new ArgumentException("位域越过当前位宽；请同时调整起始位和长度。");
        }
        var fieldLimit = count == 64 ? ulong.MaxValue : (1UL << count) - 1;
        var mask = fieldLimit << start;
        if (field)
        {
            updated = operation switch
            {
                "inspect" => original,
                "set" => original | mask,
                "clear" => original & ~mask,
                "toggle" => original ^ mask,
                "replace" => Replace(),
                _ => original
            };
        }
        else if (operation == "not")
        {
            updated = word.Unary("not", original);
        }
        else if (operation != "evaluate")
        {
            var operand = new ProgrammerExpression(word, LabInput.Text(values, "operand", "0xFF", 1024)).Evaluate();
            updated = word.Apply(operation, original, operand);
        }
        var signed = (long)word.SignedValue(updated);
        var unsigned = updated.ToString(CultureInfo.InvariantCulture);
        var hex = ProgrammerWord.Format(updated, 16).PadLeft(width / 4, '0');
        var octal = ProgrammerWord.Format(updated, 8);
        var binary = ProgrammerWord.Format(updated, 2).PadLeft(width, '0');
        var groupedBinary = string.Join(" ", Enumerable.Range(0, width / 4).Select(index => binary.Substring(index * 4, 4)));
        var little = Enumerable.Range(0, width / 8).Select(index => ((updated >> (index * 8)) & 255).ToString("X2", CultureInfo.InvariantCulture)).ToArray();
        var littleEndian = string.Join(" ", little);
        var bigEndian = string.Join(" ", little.Reverse());
        var extracted = (updated & mask) >> start;
        var decimalText = word.Signed ? signed.ToString(CultureInfo.InvariantCulture) : unsigned;
        var code = $"#include <stdint.h>\nuint{width}_t value = UINT{width}_C(0x{hex});\n";
        if (field)
        {
            code += $"#define FIELD_MASK UINT{width}_C(0x{mask:X})\n#define FIELD_SHIFT {start}u\nuint{width}_t field = (value & FIELD_MASK) >> FIELD_SHIFT;\n";
        }
        var copy = $"HEX  0x{hex}\nDEC  {decimalText}\nOCT  0o{octal}\nBIN  0b{binary}\n无符号 {unsigned}\n有符号 {signed}\n小端 {littleEndian}\n大端 {bigEndian}\n\n{code}";
        var widgets = new List<StudioX.Extensions.Abstractions.PluginPanelWidget>
        {
            new("answer", "metric", "", Json("0x" + hex)),
            Table("bases", "进制转换", ["进制", "结果"],
                [ ["HEX · 十六进制", "0x" + hex], ["DEC · 十进制", decimalText],
                  ["OCT · 八进制", "0o" + octal], ["BIN · 二进制", groupedBinary] ]),
            Text("interpretation", "整数解释", $"{width} 位 · 无符号 {unsigned} · 有符号 {signed}"),
            Text("bytes", "字节序（低地址 → 高地址）", $"小端 {littleEndian}；大端 {bigEndian}")
        };
        if (field)
        {
            widgets.Add(Text("field", "位域", $"bit {start}–{start + count - 1} · 掩码 0x{mask:X} · 位域值 {extracted}"));
            // 兼容旧宿主每表最多呈现 60 行；QWORD 分成两表，最低四位仍可观察。
            for (var offset = 0; offset < width; offset += 32)
            {
                widgets.Add(Table(offset == 0 ? "bits" : "bits-low", width <= 32 ? "逐位观察" : offset == 0 ? "逐位观察 · bit 63–32" : "逐位观察 · bit 31–0",
                    ["位", "原值", "结果", "位域"], Enumerable.Range(0, width).Reverse().Skip(offset).Take(32)
                    .Select(bit => new[] { bit.ToString(CultureInfo.InvariantCulture), ((original >> bit) & 1).ToString(), ((updated >> bit) & 1).ToString(), bit >= start && bit < start + count ? "选中" : "" })));
            }
        }
        return new(new
        {
            width,
            radix,
            signedMode = word.Signed,
            operation,
            original,
            updated,
            mask,
            extracted,
            signed,
            hex,
            unsigned,
            decimalText,
            octal,
            binary,
            littleEndian,
            bigEndian
        }, copy, widgets.ToArray());

        ulong Replace()
        {
            var operand = word.Parse(LabInput.Text(values, "operand", "0xAB", 1024).Trim());
            if (operand > fieldLimit)
            {
                throw new ArgumentException("替换值超出位域范围，不会自动截断。");
            }
            return (original & ~mask) | (operand << start);
        }
    }
}
