namespace StudioX.PluginValidation;

using System.Text.Json;
using StudioX.Application.Plugins;
using StudioX.Foundation;

/// <summary>输入模式的定义与参数边界，检查 Unicode、整数精度及显式对象范围。</summary>
internal static class SchemaChecks
{
    public static void Run(ValidationChecks checks)
    {
        RejectDefinition("""{"type":"object","additionalProperties":false,"$ref":"https://invalid.example/schema"}""", checks, "$ref cannot load external or local schema");
        RejectDefinition("""{"type":"object","additionalProperties":false,"unknown":true}""", checks, "unknown schema keyword rejected");
        RejectDefinition("""{"type":"object","properties":{}}""", checks, "object schema requires explicit additionalProperties");
        RejectDefinition("""{"type":"object","additionalProperties":false,"properties":{"list":{"type":"array"}}}""", checks, "array schema requires items");
        RejectDefinition("""{"type":"object","additionalProperties":false,"required":["missing"]}""", checks, "required must refer to a declared property");
        var schema = Parse("""
            {"type":"object","additionalProperties":false,"required":["name","values"],"properties":{
              "name":{"type":["string","null"],"minLength":1,"maxLength":1},
              "values":{"type":"array","items":{"type":"integer"},"minItems":1,"maxItems":2},
              "mode":{"type":"string","enum":["one","two"]}}}
            """);
        PluginInputSchema.ValidateDefinition(schema);
        PluginInputSchema.ValidateArguments(schema, Parse("""{"name":"😀","values":[1,2.0],"mode":"one"}"""));
        PluginInputSchema.ValidateArguments(schema, Parse("""{"name":null,"values":[100e-2]}"""));
        checks.Check(true, "valid schema supports null union, array items and Unicode scalar string length");
        RejectArguments(schema, """{"name":"ab","values":[1]}""", checks, "string scalar length overflow rejected");
        RejectArguments(schema, """{"name":"a","values":[1,2,3]}""", checks, "array length overflow rejected");
        RejectArguments(schema, """{"name":"a","values":[1.5]}""", checks, "array item type mismatch rejected");
        RejectArguments(schema, """{"name":12,"values":[1]}""", checks, "type mismatch rejected");
        RejectArguments(schema, """{"name":"a"}""", checks, "missing required property rejected");
        RejectArguments(schema, """{"name":"a","values":[1],"extra":1}""", checks, "additional property rejected");
        RejectArguments(schema, """{"name":"a","values":[1],"mode":"three"}""", checks, "enum mismatch rejected");
        var integer = Parse("""{"type":"object","additionalProperties":false,"properties":{"n":{"type":"integer"}}}""");
        PluginInputSchema.ValidateDefinition(integer);
        PluginInputSchema.ValidateArguments(integer, Parse("""{"n":9007199254740993}"""));
        PluginInputSchema.ValidateArguments(integer, Parse("""{"n":9007199254740993.0}"""));
        checks.Check(true, "integer validation preserves decimal precision beyond IEEE 754 exact range");
        RejectArguments(integer, """{"n":9007199254740992.1}""", checks, "huge decimal fraction cannot round into integer");
        RejectArguments(integer, """{"n":0.0001}""", checks, "negative decimal scale stays non-integer");
        var bound = Parse("""{"type":"object","additionalProperties":false,"properties":{"n":{"type":"integer","minimum":9007199254740993}}}""");
        PluginInputSchema.ValidateDefinition(bound);
        RejectArguments(bound, """{"n":9007199254740992}""", checks, "numeric bound compares large decimal values without IEEE 754 rounding");
    }

    private static JsonElement Parse(string text) => JsonSerializer.Deserialize<JsonElement>(text);

    private static void RejectDefinition(string text, ValidationChecks checks, string description)
    {
        try
        {
            PluginInputSchema.ValidateDefinition(Parse(text));
        }
        catch (StudioXException exception) when (exception.Code == "PLUGIN_SCHEMA")
        {
            checks.Check(true, description);
            return;
        }
        throw new InvalidOperationException("schema 未拒绝：" + description);
    }

    private static void RejectArguments(JsonElement schema, string arguments, ValidationChecks checks, string description)
    {
        try
        {
            PluginInputSchema.ValidateArguments(schema, Parse(arguments));
        }
        catch (StudioXException exception) when (exception.Code == "PLUGIN_ARGUMENTS")
        {
            checks.Check(true, description);
            return;
        }
        throw new InvalidOperationException("参数未拒绝：" + description);
    }
}
