namespace StudioX.LabPlugins;

using System.Globalization;
using System.Text.Json;
using static LabPanel;

public sealed partial class BitLabPlugin
{
    private (string Id, JsonElement Arguments) PrepareInteraction(JsonElement arguments)
    {
        var action = LabInput.Text(arguments, "action", "calculate");
        var values = arguments.TryGetProperty("values", out var submitted) ? NormalizeInputs(submitted) : CurrentInput;
        if (action is "preview" or "calculate" or "clear" or "calculator" or "bitfield")
        {
            return (action == "preview" ? "calculate" : action, Json(new
            {
                values
            }));
        }
        if (action is not ("configure" or "bit" or "shift" or "unary"))
        {
            throw new ArgumentException("未知计算器交互。");
        }
        var edited = values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        if (!arguments.TryGetProperty("values", out _) && current is not null)
        {
            // 连续即时操作承接实际结果，不能重新对旧位域原值或旧 A 操作数进行计算。
            edited["value"] = Json("0x" + Json(current.Data).GetProperty("hex").GetString());
        }
        edited["operation"] = Json(fieldMode ? "inspect" : "evaluate");
        var evaluated = Json(Calculate(Json(edited)).Data);
        var number = evaluated.GetProperty("updated").GetUInt64();
        var width = evaluated.GetProperty("width").GetInt32();
        var radix = evaluated.GetProperty("radix").GetInt32();
        if (action == "bit")
        {
            var bit = LabInput.Integer(arguments, "bit", -1, 0, width - 1);
            number ^= 1UL << bit;
        }
        else if (action is "shift" or "unary")
        {
            var arithmeticWord = new ProgrammerWord(width, radix, LabInput.Boolean(values, "signedMode"));
            var operation = LabInput.Text(arguments, "operation", "");
            number = action == "shift" && operation is "shl" or "shr" or ">>>" or "rol" or "ror"
                ? arithmeticWord.Apply(operation, number, 1)
                : action == "unary" && operation is "not" or "-" ? arithmeticWord.Unary(operation, number)
                : throw new ArgumentException("无效的即时位运算。");
        }
        else
        {
            var setting = LabInput.Text(arguments, "setting", "");
            var choice = LabInput.Text(arguments, "choice", "");
            if (setting == "width" && choice is "8" or "16" or "32" or "64")
            {
                width = int.Parse(choice, CultureInfo.InvariantCulture);
                // 与程序员计算器的位宽切换一致：用户主动缩窄时保留低位，直接输入仍严格检查范围。
                number &= width == 64 ? ulong.MaxValue : (1UL << width) - 1;
                edited["width"] = Json(choice);
                var start = Math.Min(LabInput.Integer(values, "start", 0, 0, 63), width - 1);
                edited["start"] = Json(start);
                edited["count"] = Json(Math.Min(LabInput.Integer(values, "count", 1, 1, 64), width - start));
            }
            else if (setting == "radix" && choice is "2" or "8" or "10" or "16")
            {
                radix = int.Parse(choice, CultureInfo.InvariantCulture);
                edited["radix"] = Json(choice);
            }
            else if (setting == "signedMode" && choice is "true" or "false")
            {
                edited["signedMode"] = Json(choice == "true");
            }
            else
            {
                throw new ArgumentException("无效的计算器设置。");
            }
        }
        var word = new ProgrammerWord(width, radix, LabInput.Boolean(Json(edited), "signedMode"));
        edited["value"] = Json(radix == 10 && word.Signed ? word.SignedValue(number).ToString(CultureInfo.InvariantCulture) : ProgrammerWord.Format(number, radix));
        return ("calculate", Json(new
        {
            values = edited
        }));
    }
}
