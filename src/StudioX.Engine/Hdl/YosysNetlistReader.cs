namespace StudioX.Engine.Hdl;

using System.Text.Json;
using StudioX.Foundation;

/// <summary>读取真实 Yosys JSON 网表；不从源码正则猜测逻辑或省略不认识的单元。</summary>
public static class YosysNetlistReader
{
    public static HdlModule[] Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var modules = document.RootElement.GetProperty("modules");
        return modules.EnumerateObject().Select(module =>
        {
            var value = module.Value;
            var names = value.GetProperty("netnames");
            var ports = value.GetProperty("ports").EnumerateObject().Select(port =>
                ReadPort(port.Name, port.Value.GetProperty("direction").GetString()!, Bits(port.Value.GetProperty("bits")),
                    port.Value, names.TryGetProperty(port.Name, out var signal) ? Attribute(signal, "src") : null)).ToArray();
            var signals = names.EnumerateObject()
                .OrderBy(signal => signal.Value.GetProperty("hide_name").GetInt32())
                .Select(signal => ReadPort(signal.Name, "wire", Bits(signal.Value.GetProperty("bits")), signal.Value, Attribute(signal.Value, "src")))
                .ToArray();
            var cells = value.GetProperty("cells").EnumerateObject().Select(cell =>
            {
                var directions = cell.Value.GetProperty("port_directions");
                var type = cell.Value.GetProperty("type").GetString()!;
                var formalPorts = modules.TryGetProperty(type, out var definition) ? definition.GetProperty("ports") : default;
                var connections = cell.Value.GetProperty("connections").EnumerateObject().Select(port =>
                {
                    if (!directions.TryGetProperty(port.Name, out var direction))
                    {
                        throw new StudioXException("HDL_PORT_DIRECTION", $"{cell.Name}.{port.Name} 没有端口方向，不能绘制可靠的连线。");
                    }
                    var bits = Bits(port.Value);
                    return formalPorts.ValueKind == JsonValueKind.Object && formalPorts.TryGetProperty(port.Name, out var formal)
                        ? ReadPort(port.Name, direction.GetString()!, bits, formal, null)
                        : new HdlPort(port.Name, direction.GetString()!, bits);
                }).ToArray();
                var parameters = cell.Value.GetProperty("parameters").EnumerateObject()
                    .ToDictionary(parameter => parameter.Name, parameter => parameter.Value.ToString());
                return new HdlCell(cell.Name, type, connections,
                    parameters, Attribute(cell.Value, "src"));
            }).ToArray();
            return new HdlModule(module.Name, ports, cells, signals, Attribute(value, "src"),
                Attribute(value, "top")?.EndsWith('1') == true, Attribute(value, "blackbox")?.EndsWith('1') == true);
        }).OrderByDescending(module => module.IsTop).ThenBy(module => module.Name, StringComparer.Ordinal).ToArray();
    }

    private static HdlPort ReadPort(string name, string direction, string[] bits, JsonElement declaration, string? source) =>
        new(name, direction, bits, declaration.TryGetProperty("offset", out var offset) ? offset.GetInt32() : 0,
            declaration.TryGetProperty("upto", out var upto) && upto.GetInt32() == 1)
        {
            Source = source,
            IsGenerated = declaration.TryGetProperty("hide_name", out var hidden) && hidden.GetInt32() == 1
        };

    private static string[] Bits(JsonElement value) => value.EnumerateArray().Select(bit =>
        bit.ValueKind == JsonValueKind.Number ? "n" + bit.GetRawText() : bit.GetString()!).ToArray();

    private static string? Attribute(JsonElement value, string name) =>
        value.TryGetProperty("attributes", out var attributes) && attributes.TryGetProperty(name, out var attribute)
            ? attribute.ToString() : null;
}
