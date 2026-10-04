namespace StudioX.Application.Plugins;

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using StudioX.Foundation;

/// <summary>实现有界 JSON Schema 子集；定义和参数使用同一规则，不解析引用或外部资源。</summary>
public static class PluginInputSchema
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "type", "properties", "required", "additionalProperties", "items", "enum", "const",
        "minLength", "maxLength", "minItems", "maxItems", "minimum", "maximum",
        "exclusiveMinimum", "exclusiveMaximum", "title", "description", "default", "examples"
    };
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "object", "array", "string", "number", "integer", "boolean", "null"
    };

    public static void ValidateDefinition(JsonElement schema)
    {
        PluginContributionValidator.ValidateJson(schema, 64 * 1024);
        ValidateExactNumbers(schema);
        ValidateNode(schema, 0);
        if (schema.GetProperty("type").ValueKind != JsonValueKind.String || schema.GetProperty("type").GetString() != "object")
        {
            throw Invalid("Agent 工具输入根 schema 必须为 object。");
        }
    }

    public static void ValidateArguments(JsonElement schema, JsonElement arguments)
    {
        PluginContributionValidator.ValidateJson(arguments);
        ValidateExactNumbers(arguments);
        MatchNode(schema, arguments, "$", 0);
    }

    private static void ValidateNode(JsonElement schema, int depth)
    {
        if (depth > 16 || schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type))
        {
            throw Invalid("每个 schema 节点必须为声明 type 的对象，深度最多 16。");
        }
        foreach (var property in schema.EnumerateObject())
        {
            if (!Keywords.Contains(property.Name))
            {
                throw Invalid("不支持的 schema 关键字：" + property.Name);
            }
        }
        var declared = ReadTypes(type);
        foreach (var annotation in new[] { "title", "description" })
        {
            if (schema.TryGetProperty(annotation, out var value) &&
                (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 8192))
            {
                throw Invalid(annotation + "必须为有界字符串。");
            }
        }
        if (schema.TryGetProperty("examples", out var examples) &&
            (examples.ValueKind != JsonValueKind.Array || examples.GetArrayLength() > 16))
        {
            throw Invalid("examples 必须为最多 16 项的数组。");
        }
        if (schema.TryGetProperty("enum", out var values) &&
            (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() is < 1 or > 256))
        {
            throw Invalid("enum 必须为 1 到 256 项的数组。");
        }
        if (declared.Contains("object"))
        {
            if (!schema.TryGetProperty("additionalProperties", out var additional) ||
                additional.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw Invalid("object schema 必须显式声明布尔 additionalProperties。");
            }
            if (schema.TryGetProperty("properties", out var properties))
            {
                if (properties.ValueKind != JsonValueKind.Object || properties.EnumerateObject().Count() > 128)
                {
                    throw Invalid("properties 必须为最多 128 个属性的对象。");
                }
                foreach (var property in properties.EnumerateObject())
                {
                    if (string.IsNullOrEmpty(property.Name) || property.Name.Length > 128)
                    {
                        throw Invalid("参数属性名称必须为 1 到 128 个字符。");
                    }
                    ValidateNode(property.Value, depth + 1);
                }
            }
            if (schema.TryGetProperty("required", out var required))
            {
                if (required.ValueKind != JsonValueKind.Array || required.GetArrayLength() > 128)
                {
                    throw Invalid("required 必须为有界数组。");
                }
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var name in required.EnumerateArray())
                {
                    if (name.ValueKind != JsonValueKind.String || !names.Add(name.GetString()!) ||
                        !schema.TryGetProperty("properties", out var props) || !props.TryGetProperty(name.GetString()!, out _))
                    {
                        throw Invalid("required 必须引用已声明且不重复的属性。");
                    }
                }
            }
        }
        else
        {
            Forbid(schema, "properties", "required", "additionalProperties");
        }
        if (declared.Contains("array"))
        {
            if (!schema.TryGetProperty("items", out var items))
            {
                throw Invalid("array schema 必须声明单个 items schema。");
            }
            ValidateNode(items, depth + 1);
            ValidateCountBounds(schema, "minItems", "maxItems", 2048);
        }
        else
        {
            Forbid(schema, "items", "minItems", "maxItems");
        }
        if (declared.Contains("string"))
        {
            ValidateCountBounds(schema, "minLength", "maxLength", 65536);
        }
        else
        {
            Forbid(schema, "minLength", "maxLength");
        }
        if (declared.Contains("number") || declared.Contains("integer"))
        {
            foreach (var keyword in new[] { "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum" })
            {
                if (schema.TryGetProperty(keyword, out var bound) &&
                    (bound.ValueKind != JsonValueKind.Number || !bound.TryGetDouble(out var number) || !double.IsFinite(number)))
                {
                    throw Invalid(keyword + "必须为有限数值。");
                }
                if (schema.TryGetProperty(keyword, out var exactBound))
                {
                    _ = ExactNumber.Parse(exactBound);
                }
            }
            foreach (var lowerKeyword in new[] { "minimum", "exclusiveMinimum" })
            {
                foreach (var upperKeyword in new[] { "maximum", "exclusiveMaximum" })
                {
                    if (schema.TryGetProperty(lowerKeyword, out var lower) && schema.TryGetProperty(upperKeyword, out var upper))
                    {
                        var comparison = ExactNumber.Parse(lower).CompareTo(ExactNumber.Parse(upper));
                        if (comparison > 0 || (comparison == 0 && (lowerKeyword == "exclusiveMinimum" || upperKeyword == "exclusiveMaximum")))
                        {
                            throw Invalid("schema 数值上下界没有可接受范围。");
                        }
                    }
                }
            }
        }
        else
        {
            Forbid(schema, "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum");
        }
    }

    private static void MatchNode(JsonElement schema, JsonElement value, string path, int depth)
    {
        if (depth > 16 || !ReadTypes(schema.GetProperty("type")).Any(type => IsType(value, type)))
        {
            throw Arguments(path + "类型与 schema 不匹配。");
        }
        if (schema.TryGetProperty("enum", out var values) && !values.EnumerateArray().Any(item => JsonEqual(item, value)))
        {
            throw Arguments(path + "不在 enum 中。");
        }
        if (schema.TryGetProperty("const", out var constant) && !JsonEqual(constant, value))
        {
            throw Arguments(path + "与 const 不匹配。");
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var name in required.EnumerateArray())
                {
                    if (!value.TryGetProperty(name.GetString()!, out _))
                    {
                        throw Arguments(path + "缺少必填属性：" + name.GetString());
                    }
                }
            }
            foreach (var property in value.EnumerateObject())
            {
                if (schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty(property.Name, out var child))
                {
                    MatchNode(child, property.Value, path + "." + property.Name, depth + 1);
                }
                else if (!schema.GetProperty("additionalProperties").GetBoolean())
                {
                    throw Arguments(path + "含未声明属性：" + property.Name);
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            MatchCount(schema, value.GetArrayLength(), "minItems", "maxItems", path);
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                MatchNode(schema.GetProperty("items"), item, path + "[" + index++ + "]", depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            // JSON Schema 长度以 Unicode 标量计数，不能把 UTF-16 代理项误算为两个字符。
            MatchCount(schema, value.GetString()!.EnumerateRunes().Count(), "minLength", "maxLength", path);
        }
        else if (value.ValueKind == JsonValueKind.Number)
        {
            var number = ExactNumber.Parse(value);
            if ((schema.TryGetProperty("minimum", out var min) && number.CompareTo(ExactNumber.Parse(min)) < 0) ||
                (schema.TryGetProperty("maximum", out var max) && number.CompareTo(ExactNumber.Parse(max)) > 0) ||
                (schema.TryGetProperty("exclusiveMinimum", out var exclusiveMin) && number.CompareTo(ExactNumber.Parse(exclusiveMin)) <= 0) ||
                (schema.TryGetProperty("exclusiveMaximum", out var exclusiveMax) && number.CompareTo(ExactNumber.Parse(exclusiveMax)) >= 0))
            {
                throw Arguments(path + "超过数值范围。");
            }
        }
    }

    private static HashSet<string> ReadTypes(JsonElement type)
    {
        var values = type.ValueKind == JsonValueKind.String ? new[] { type } :
            type.ValueKind == JsonValueKind.Array && type.GetArrayLength() is >= 1 and <= 7 ? type.EnumerateArray().ToArray() : [];
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (values.Length == 0 || values.Any(value => value.ValueKind != JsonValueKind.String ||
            !Types.Contains(value.GetString()!) || !result.Add(value.GetString()!)))
        {
            throw Invalid("type 必须为已支持类型或不重复的类型数组。");
        }
        return result;
    }

    private static bool IsType(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
        "integer" => IsInteger(value),
        _ => false
    };

    private static bool IsInteger(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            return false;
        }
        var number = ExactNumber.Parse(value);
        return number.Sign == 0 || number.Exponent >= 0;
    }

    private static bool JsonEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }
        if (left.ValueKind == JsonValueKind.Number)
        {
            return ExactNumber.Parse(left).CompareTo(ExactNumber.Parse(right)) == 0;
        }
        if (left.ValueKind == JsonValueKind.Array)
        {
            return left.GetArrayLength() == right.GetArrayLength() &&
                left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => JsonEqual(pair.First, pair.Second));
        }
        if (left.ValueKind == JsonValueKind.Object)
        {
            return left.EnumerateObject().Count() == right.EnumerateObject().Count() &&
                left.EnumerateObject().All(property => right.TryGetProperty(property.Name, out var value) && JsonEqual(property.Value, value));
        }
        return JsonElement.DeepEquals(left, right);
    }

    private static void ValidateExactNumbers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            _ = ExactNumber.Parse(value);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                ValidateExactNumbers(item);
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                ValidateExactNumbers(property.Value);
            }
        }
    }

    private static void ValidateCountBounds(JsonElement schema, string minimum, string maximum, int limit)
    {
        foreach (var keyword in new[] { minimum, maximum })
        {
            if (schema.TryGetProperty(keyword, out var bound) &&
                (bound.ValueKind != JsonValueKind.Number || !bound.TryGetInt32(out var number) || number < 0 || number > limit))
            {
                throw Invalid(keyword + "必须为 0 到 " + limit + " 的整数。");
            }
        }
        if (schema.TryGetProperty(minimum, out var min) && schema.TryGetProperty(maximum, out var max) && min.GetInt32() > max.GetInt32())
        {
            throw Invalid(minimum + "不能超过" + maximum + "。");
        }
    }

    private static void MatchCount(JsonElement schema, int count, string minimum, string maximum, string path)
    {
        var hardLimit = maximum == "maxItems" ? 2048 : 65536;
        if (count > hardLimit || (schema.TryGetProperty(minimum, out var min) && count < min.GetInt32()) ||
            (schema.TryGetProperty(maximum, out var max) && count > max.GetInt32()))
        {
            throw Arguments(path + "长度超过 schema 范围。");
        }
    }

    private static void Forbid(JsonElement schema, params string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (schema.TryGetProperty(keyword, out _))
            {
                throw Invalid(keyword + "不适用于该 type。");
            }
        }
    }

    private static StudioXException Invalid(string message) => new("PLUGIN_SCHEMA", message);
    private static StudioXException Arguments(string message) => new("PLUGIN_ARGUMENTS", message);

    private readonly record struct ExactNumber(int Sign, string Digits, long Exponent)
    {
        public static ExactNumber Parse(JsonElement value)
        {
            var raw = value.GetRawText();
            if (raw.Length > 4096)
            {
                throw new StudioXException("PLUGIN_NUMBER_LIMIT", "schema 数值最多 4096 个字符。");
            }
            var exponentIndex = raw.IndexOfAny(['e', 'E']);
            var exponent = 0;
            if (exponentIndex >= 0 && (!int.TryParse(raw[(exponentIndex + 1)..], NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out exponent) || exponent is < -1000000 or > 1000000))
            {
                throw new StudioXException("PLUGIN_NUMBER_LIMIT", "schema 数值指数的绝对值最多 1000000。");
            }
            var mantissa = exponentIndex < 0 ? raw : raw[..exponentIndex];
            var point = mantissa.IndexOf('.');
            var fractionalDigits = point < 0 ? 0 : mantissa.Length - point - 1;
            var digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('-').TrimStart('0');
            if (digits.Length == 0)
            {
                return new(0, "0", 0);
            }
            var withoutTrailingZeros = digits.TrimEnd('0');
            var decimalExponent = (long)exponent - fractionalDigits + digits.Length - withoutTrailingZeros.Length;
            // 系数大小有界；比较指数位权与有效数字，不构造 10 的巨大指数幂。
            var coefficient = BigInteger.Parse(withoutTrailingZeros, NumberStyles.None, CultureInfo.InvariantCulture);
            return new(raw[0] == '-' ? -1 : 1, coefficient.ToString(CultureInfo.InvariantCulture), decimalExponent);
        }

        public int CompareTo(ExactNumber other)
        {
            var signComparison = Sign.CompareTo(other.Sign);
            if (signComparison != 0 || Sign == 0)
            {
                return signComparison;
            }
            var magnitudeComparison = (Digits.Length + Exponent).CompareTo(other.Digits.Length + other.Exponent);
            if (magnitudeComparison != 0)
            {
                return magnitudeComparison * Sign;
            }
            var length = Math.Max(Digits.Length, other.Digits.Length);
            for (var index = 0; index < length; index++)
            {
                var left = index < Digits.Length ? Digits[index] : '0';
                var right = index < other.Digits.Length ? other.Digits[index] : '0';
                var digitComparison = left.CompareTo(right);
                if (digitComparison != 0)
                {
                    return digitComparison * Sign;
                }
            }
            return 0;
        }
    }
}
