namespace StudioX.Application.Plugins;

using System.Text.Json;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

/// <summary>校验专用计算器视图的数据边界，拒绝未知格式和会造成无界界面的内容。</summary>
public static class PluginProgrammerCalculator
{
    public static PluginProgrammerCalculatorState Parse(JsonElement value)
    {
        PluginContributionValidator.ValidateJson(value, 32 * 1024);
        PluginProgrammerCalculatorState? state;
        try
        {
            state = value.Deserialize<PluginProgrammerCalculatorState>(JsonStore.Options);
        }
        catch (JsonException error)
        {
            throw new StudioXException("PLUGIN_CALCULATOR", "程序员计算器数据格式无效。", error);
        }
        if (state is null || state.FormatVersion != 1 || state.RequestId < 0 || state.Width is not (8 or 16 or 32 or 64) ||
            state.Radix is not (2 or 8 or 10 or 16) || state.Start < 0 || state.Count < 1 ||
            state.Start >= state.Width || state.Count > state.Width - state.Start ||
            state.Operation is not ("inspect" or "set" or "clear" or "toggle" or "replace"))
        {
            throw new StudioXException("PLUGIN_CALCULATOR", "计算器版本、位宽、进制或位域范围无效。");
        }
        var strings = new[] { state.Expression, state.Hex, state.DecimalText, state.Octal, state.Binary,
            state.Unsigned, state.Signed, state.LittleEndian, state.BigEndian, state.CopyText, state.Operand, state.FieldValue, state.FieldMask };
        if (strings.Any(text => text is null || text.Length > 4096 || text.Contains('\0')) ||
            state.Error is { Length: > 2048 } || state.Expression.Length > 1024 || state.Operand.Length > 1024 ||
            state.Hex.Length != state.Width / 4 || !state.Hex.All(Uri.IsHexDigit) ||
            state.Binary.Length != state.Width || !state.Binary.All(bit => bit is '0' or '1'))
        {
            throw new StudioXException("PLUGIN_CALCULATOR", "计算器显示文本或逐位数据无效。");
        }
        return state;
    }
}
