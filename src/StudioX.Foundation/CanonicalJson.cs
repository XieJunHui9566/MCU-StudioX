namespace StudioX.Foundation;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>按 JCS 规范化有界 JSON；摘要不受空白和对象属性顺序影响。</summary>
public static class CanonicalJson
{
    public static string Hash(JsonElement value) => Convert.ToHexStringLower(SHA256.HashData(Encode(value)));

    public static byte[] Encode(JsonElement value)
    {
        var text = new StringBuilder();
        Write(value, text, 0);
        return new UTF8Encoding(false, true).GetBytes(text.ToString());
    }

    private static void Write(JsonElement value, StringBuilder text, int depth)
    {
        if (depth > 32)
        {
            throw new StudioXException("JSON_DEPTH", "配置 JSON 超过 32 层。");
        }
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
                if (properties.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                {
                    throw new StudioXException("JSON_DUPLICATE", "JSON 包含重复属性。");
                }
                text.Append('{');
                for (var i = 0; i < properties.Length; i++)
                {
                    if (i > 0)
                    {
                        text.Append(',');
                    }
                    String(properties[i].Name, text);
                    text.Append(':');
                    Write(properties[i].Value, text, depth + 1);
                }
                text.Append('}');
                break;
            case JsonValueKind.Array:
                text.Append('[');
                var first = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!first)
                    {
                        text.Append(',');
                    }
                    first = false;
                    Write(item, text, depth + 1);
                }
                text.Append(']');
                break;
            case JsonValueKind.String:
                String(value.GetString()!, text);
                break;
            case JsonValueKind.Number:
                var number = value.GetDouble();
                if (!double.IsFinite(number))
                {
                    throw new StudioXException("JSON_NUMBER", "JSON 数值必须有限。");
                }
                text.Append(Number(number));
                break;
            case JsonValueKind.True:
                text.Append("true");
                break;
            case JsonValueKind.False:
                text.Append("false");
                break;
            case JsonValueKind.Null:
                text.Append("null");
                break;
            default:
                throw new StudioXException("JSON_VALUE", "JSON 值未定义。");
        }
    }

    private static void String(string value, StringBuilder text)
    {
        text.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '"':
                    text.Append("\\\"");
                    break;
                case '\\':
                    text.Append("\\\\");
                    break;
                case '\b':
                    text.Append("\\b");
                    break;
                case '\t':
                    text.Append("\\t");
                    break;
                case '\n':
                    text.Append("\\n");
                    break;
                case '\f':
                    text.Append("\\f");
                    break;
                case '\r':
                    text.Append("\\r");
                    break;
                default:
                    if (c < 32)
                    {
                        text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(c))
                    {
                        if (++i >= value.Length || !char.IsLowSurrogate(value[i]))
                        {
                            throw new StudioXException("JSON_UNICODE", "JSON 包含无效 Unicode。");
                        }
                        text.Append(c).Append(value[i]);
                    }
                    else if (char.IsLowSurrogate(c))
                    {
                        throw new StudioXException("JSON_UNICODE", "JSON 包含无效 Unicode。");
                    }
                    else
                    {
                        text.Append(c);
                    }
                    break;
            }
        }
        text.Append('"');
    }

    private static string Number(double value)
    {
        if (value == 0)
        {
            return "0";
        }
        var sign = value < 0 ? "-" : "";
        var raw = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        var parts = raw.Split('e');
        var exponent = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        var dot = parts[0].IndexOf('.');
        var digits = parts[0].Replace(".", "", StringComparison.Ordinal);
        var position = (dot < 0 ? digits.Length : dot) + exponent;
        // 最短十进制的有效数字保持不变，按 ECMAScript 的固定/科学记数范围输出。
        while (digits.Length > 1 && digits[0] == '0')
        {
            digits = digits[1..];
            position--;
        }
        digits = digits.TrimEnd('0');
        if (digits.Length == 0)
        {
            return "0";
        }
        if (position > 0 && position <= 21)
        {
            return sign + (position >= digits.Length ? digits + new string('0', position - digits.Length) : digits.Insert(position, "."));
        }
        if (position <= 0 && position > -6)
        {
            return sign + "0." + new string('0', -position) + digits;
        }
        var power = position - 1;
        return sign + digits[0] + (digits.Length > 1 ? "." + digits[1..] : "") + "e" +
            (power >= 0 ? "+" : "") + power.ToString(CultureInfo.InvariantCulture);
    }
}
