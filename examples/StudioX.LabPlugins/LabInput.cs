namespace StudioX.LabPlugins;

using System.Globalization;
using System.Text.Json;

internal static class LabInput
{
    internal static string Text(JsonElement input, string name, string fallback, int maximum = 4096)
    {
        if (!input.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException($"{name} 必须是文本。");
        var text = value.GetString()!;
        if (text.Length > maximum) throw new ArgumentException($"{name} 最多 {maximum} 个字符。");
        return text;
    }

    internal static double Number(JsonElement input, string name, double fallback, double minimum, double maximum)
    {
        if (!input.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) ||
            !double.IsFinite(number) || number < minimum || number > maximum)
            throw new ArgumentException($"{name} 须在 {minimum.ToString(CultureInfo.InvariantCulture)}–{maximum.ToString(CultureInfo.InvariantCulture)} 之间。");
        return number;
    }

    internal static int Integer(JsonElement input, string name, int fallback, int minimum, int maximum)
    {
        var number = Number(input, name, fallback, minimum, maximum);
        if (number != Math.Truncate(number)) throw new ArgumentException($"{name} 必须是整数。");
        return (int)number;
    }

    internal static bool Boolean(JsonElement input, string name, bool fallback = false)
    {
        if (!input.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException($"{name} 必须是布尔值。");
        return value.GetBoolean();
    }

    internal static ulong Unsigned(string text)
    {
        text = text.Trim();
        var radix = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 :
            text.StartsWith("0b", StringComparison.OrdinalIgnoreCase) ? 2 : 10;
        var digits = radix == 10 ? text : text[2..];
        if (digits.Length is < 1 or > 32) throw new ArgumentException("请输入十进制、0x 十六进制或 0b 二进制无符号整数。");
        ulong result = 0;
        foreach (var character in digits)
        {
            var digit = character is >= '0' and <= '9' ? character - '0' :
                character is >= 'a' and <= 'f' ? character - 'a' + 10 :
                character is >= 'A' and <= 'F' ? character - 'A' + 10 : -1;
            if (digit < 0 || digit >= radix || result > (ulong.MaxValue - (uint)digit) / (uint)radix)
                throw new ArgumentException("整数包含非法字符或超过 64 位范围。");
            result = result * (uint)radix + (uint)digit;
        }
        return result;
    }
}
