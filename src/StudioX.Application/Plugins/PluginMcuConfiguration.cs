namespace StudioX.Application.Plugins;

using System.Text.Json;
using System.Text.Json.Serialization;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

/// <summary>图形配置预览的视图边界；不在通用宿主推断芯片规则。</summary>
public static class PluginMcuConfiguration
{
    public static PluginMcuConfigurationState Parse(JsonElement value)
    {
        PluginContributionValidator.ValidateJson(value, 256 * 1024);
        var options = new JsonSerializerOptions(JsonStore.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var state = value.Deserialize<PluginMcuConfigurationState>(options)
            ?? throw Invalid("视图为空。");
        if (state.FormatVersion != 1 || state.Pins is not { Length: > 0 and <= 512 } ||
            state.Pins.Any(pin => pin is null) || state.Clocks is not { Length: <= 512 } || state.Clocks.Any(clock => clock is null) ||
            state.Fields is not { Length: <= 64 } || state.Fields.Any(field => field is null) ||
            state.Diagnostics is not { Length: <= 64 } || !Finite(state.Width) || !Finite(state.Height) || state.Width <= 0 || state.Height <= 0)
        {
            throw Invalid("配置视图的版本、容量或尺寸无效。");
        }
        Text(state.Title, 256);
        Text(state.Subtitle, 2048);
        Text(state.PackageName, 128);
        Text(state.Status, 2048);
        Text(state.GeneratedCode, 32000);
        Text(state.SelectedPinId, 128);
        Unique(state.Pins.Select(pin => pin.Id));
        Unique(state.Pins.Select(pin => pin.Position));
        Unique(state.Clocks.Select(clock => clock.Id));
        Unique(state.Fields.Select(field => field.Id));
        foreach (var pin in state.Pins)
        {
            Text(pin.Name, 128);
            Text(pin.Label, 128);
            Text(pin.FunctionId, 128);
            if (pin.Functions is not { Length: <= 32 } || !Finite(pin.X) || !Finite(pin.Y) || pin.X < 0 || pin.X > state.Width || pin.Y < 0 || pin.Y > state.Height ||
                pin.Side is not ("left" or "right" or "top" or "bottom" or "center") ||
                pin.Category is not ("io" or "supply" or "ground" or "reset" or "oscillator" or "reserved" or "nc" or "other"))
            {
                throw Invalid("引脚功能、类别或坐标无效。");
            }
            Options(pin.Functions);
            if (pin.FunctionId.Length > 0 && !pin.Functions.Any(option => option.Value == pin.FunctionId))
            {
                throw Invalid("选择了未声明的引脚功能。");
            }
        }
        if (state.SelectedPinId.Length > 0 && !state.Pins.Any(pin => pin.Id == state.SelectedPinId))
        {
            throw Invalid("选中引脚不存在。");
        }
        foreach (var clock in state.Clocks)
        {
            Text(clock.Name, 128);
            Text(clock.Value, 256);
            Text(clock.Symbol, 32);
            Text(clock.Detail, 256);
            if (!Finite(clock.X) || !Finite(clock.Y) || clock.X < 0 || clock.X > 100000 || clock.Y < 0 || clock.Y > 100000 || clock.InputIds is not { Length: <= 32 } ||
                clock.InputIds.Any(id => id == clock.Id || !state.Clocks.Any(node => node.Id == id)) ||
                clock.Kind is not ("block" or "source" or "multiplexer" or "divider" or "junction" or "sink") ||
                clock.X > 2400 || clock.Y > 1600 ||
                clock.ActiveInputIds is { } active && (active.Length > clock.InputIds.Length || active.Distinct(StringComparer.Ordinal).Count() != active.Length || active.Any(id => !clock.InputIds.Contains(id, StringComparer.Ordinal))))
            {
                throw Invalid("时钟节点或引用无效。");
            }
        }
        foreach (var field in state.Fields)
        {
            Text(field.Section, 128);
            Text(field.Label, 256);
            Text(field.Value, 128);
            Text(field.ClockNodeId, 128);
            if (field.ClockNodeId.Length > 0 && (field.Page != "clock" || !state.Clocks.Any(clock => clock.Id == field.ClockNodeId) ||
                state.Fields.Count(other => other.ClockNodeId == field.ClockNodeId) != 1))
            {
                throw Invalid("时钟参数必须绑定唯一的已声明节点。");
            }
            if (field.Page is not ("peripherals" or "clock") || field.Kind is not ("text" or "number" or "select" or "editableSelect" or "boolean") || field.Options is not { Length: <= 128 })
            {
                throw Invalid("配置字段无效。");
            }
            Options(field.Options);
            if (field.Kind == "select" && !field.Options.Any(option => option.Value == field.Value))
            {
                throw Invalid("配置选项不存在。");
            }
        }
        foreach (var item in state.Diagnostics)
        {
            Text(item, 4096);
        }
        return state;
    }

    private static void Options(PluginMcuOption[] options)
    {
        if (options.Any(option => option is null))
        {
            throw Invalid("选项为空。");
        }
        Unique(options.Select(option => option.Value));
        foreach (var option in options)
        {
            Text(option.Label, 256);
        }
    }

    private static void Unique(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            Text(value, 128);
            if (value.Length == 0 || !seen.Add(value))
            {
                throw Invalid("稳定 ID 为空或重复。");
            }
        }
    }

    private static bool Finite(double value) => double.IsFinite(value) && value <= 100000;
    private static void Text(string value, int maximum)
    {
        if (value is null || value.Length > maximum || value.Contains('\0'))
        {
            throw Invalid("配置文本为空或超限。");
        }
    }
    private static StudioXException Invalid(string message) => new("PLUGIN_MCU_CONFIGURATION", message);
}
