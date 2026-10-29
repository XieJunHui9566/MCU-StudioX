namespace StudioX.StcConfigurator;

public sealed partial class StcConfigurationModel
{
    // 名字会成为全局 C 宏，不能遮蔽器件寄存器、C 关键字或生成器自身的符号。
    private static readonly HashSet<string> ReservedPinLabels = new(
        ("auto break case char const continue default do double else enum extern float for goto if inline int long register restrict return short signed sizeof static struct switch typedef union unsigned void volatile while " +
         "bool true false NULL main saved_ea IAP15L2K61S2 USART1_printf USART2_printf Delay_ms XMS " +
         "int8_t uint8_t int16_t uint16_t int32_t uint32_t int64_t uint64_t intptr_t uintptr_t intmax_t uintmax_t " +
         "int_least8_t uint_least8_t int_least16_t uint_least16_t int_least32_t uint_least32_t int_least64_t uint_least64_t " +
         "int_fast8_t uint_fast8_t int_fast16_t uint_fast16_t int_fast32_t uint_fast32_t int_fast64_t uint_fast64_t")
        .Split(' ').Concat(Asset("stc15-register-symbols.json")["symbols"]!.AsArray().Select(item => item!.GetValue<string>())), StringComparer.Ordinal);

    private static string? PinLabelError(string label)
    {
        if (label.Length == 0)
        {
            return null;
        }
        if (label.Length > 32 || !char.IsAsciiLetter(label[0]) && label[0] != '_' || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
        {
            return "引脚名称须为 32 字符内的 C 标识符，以字母或下划线开头，仅含字母、数字和下划线。";
        }
        if (ReservedPinLabels.Contains(label) || label.StartsWith("STUDIOX_", StringComparison.Ordinal) || label.StartsWith("sx_", StringComparison.Ordinal) || label.StartsWith("SX_", StringComparison.Ordinal) ||
            label.StartsWith("__", StringComparison.Ordinal) || label.Length > 1 && label[0] == '_' && char.IsAsciiLetterUpper(label[1]))
        {
            return "引脚名称“" + label + "”与 C、器件头文件或生成代码的保留符号冲突。";
        }
        return null;
    }

    private static void ValidatePinLabel(string label)
    {
        if (PinLabelError(label) is { } error)
        {
            throw new ArgumentException(error);
        }
    }

    private string PinAliases()
    {
        return string.Concat(Pins.Where(pin => Text(pin!, "label").Length > 0).OrderBy(pin => PinName(pin!), StringComparer.Ordinal).Select(pin =>
        {
            var label = Text(pin!, "label");
            return "#ifdef " + label + "\n#error Pin_label_" + label + "_already_defined\n#endif\n#define " + label + " " + PinName(pin!).Replace(".", "", StringComparison.Ordinal) + "\n";
        }));
    }
}
