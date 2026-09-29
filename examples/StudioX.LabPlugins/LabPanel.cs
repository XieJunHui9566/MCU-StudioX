namespace StudioX.LabPlugins;

using System.Text.Json;
using StudioX.Extensions.Abstractions;

internal static class LabPanel
{
    internal static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    internal static PluginPanelWidget Text(string id, string label, string value) => new(id, "text", label, Json(value));
    internal static PluginPanelWidget Input(string id, string label, string value) => new(id, "input", label, Json(value));
    internal static PluginPanelWidget Number(string id, string label, double value) => new(id, "number", label, Json(value));
    internal static PluginPanelWidget Check(string id, string label, bool value) => new(id, "checkbox", label, Json(value));
    internal static PluginPanelWidget Select(string id, string label, string selected, params (string Value, string Label)[] options) =>
        new(id, "select", label, Json(new { options = options.Select(option => new { value = option.Value, label = option.Label }), selected }));
    internal static PluginPanelWidget Table(string id, string label, string[] columns, IEnumerable<string[]> rows) =>
        new(id, "table", label, Json(rows), Columns: columns);
    internal static JsonElement Schema(Dictionary<string, object> properties) =>
        Json(new { type = "object", properties, additionalProperties = false });
    internal static object StringSchema(int maxLength) => new { type = "string", maxLength };
    internal static object EnumSchema(params string[] values) => new { type = "string", @enum = values };
    internal static object NumberSchema(double min, double max, bool integer = false) =>
        new { type = integer ? "integer" : "number", minimum = min, maximum = max };
}
