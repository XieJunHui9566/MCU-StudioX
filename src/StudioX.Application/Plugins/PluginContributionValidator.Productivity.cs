namespace StudioX.Application.Plugins;

using System.Text.Json;
using StudioX.Extensions;
using StudioX.Extensions.Abstractions;

public static partial class PluginContributionValidator
{
    private static readonly string[] SupportedEvents = ["project.opened", "project.closing", "document.opened", "document.changed", "document.saved", "document.closed", "settings.changed"];
    private static void ValidateProductivity(PluginManifest manifest, PluginContribution contribution)
    {
        if (contribution.Settings is null || contribution.Events is null || contribution.Languages is null || contribution.DebugAdapters is null ||
            contribution.Settings.Length > 64 || contribution.Events.Length > 7 || contribution.Languages.Length > 16 || contribution.DebugAdapters.Length > 16)
        {
            throw Invalid("API 3 贡献超过数量限制。");
        }
        var counts = new[] { ("settings", contribution.Settings.Length), ("events", contribution.Events.Length), ("languages", contribution.Languages.Length), ("debugAdapters", contribution.DebugAdapters.Length) };
        foreach (var (capability, count) in counts)
        {
            if (count > 0 && manifest.ApiVersion != 3)
            {
                throw Invalid("新扩展贡献需要 API 3。");
            }
            RequireCapability(manifest, capability, count);
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setting in contribution.Settings)
        {
            if (setting is null || !ids.Add(Identifier(setting.Id)))
            {
                throw Invalid("设置 ID 重复或无效。");
            }
            Text(setting.Title, 256, "设置标题");
            Text(setting.Description, 2048, "设置说明", true);
            if (setting.Minimum > setting.Maximum)
            {
                throw Invalid("设置范围无效。");
            }
            ValidateSetting(setting, setting.Default);
        }
        if (contribution.Events.Distinct(StringComparer.Ordinal).Count() != contribution.Events.Length || contribution.Events.Any(e => !SupportedEvents.Contains(e)))
        {
            throw Invalid("工程事件声明重复或不受支持。");
        }
        ids.Clear();
        foreach (var language in contribution.Languages)
        {
            if (language is null || !ids.Add(Identifier(language.Id)) || language.Extensions is null || language.Extensions.Length is < 1 or > 16)
            {
                throw Invalid("语言声明无效。");
            }
            Text(language.DisplayName, 256, "语言名称");
            if (language.Extensions.Any(e => e is null || e.Length is < 2 or > 24 || e[0] != '.' || e.Skip(1).Any(c => !char.IsAsciiLetterOrDigit(c))))
            {
                throw Invalid("语言扩展名必须为点加字母数字。");
            }
        }
        ids.Clear();
        foreach (var adapter in contribution.DebugAdapters)
        {
            if (adapter is null || !ids.Add(Identifier(adapter.Id)))
            {
                throw Invalid("调试适配器 ID 无效。");
            }
            Text(adapter.DisplayName, 256, "调试适配器名称");
        }
    }
    public static void ValidateSetting(PluginSettingDefinition definition, JsonElement value)
    {
        ValidateJson(value, 8192);
        var valid = definition.Type switch
        {
            "string" => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 4096,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => value.TryGetInt32Safe(out var number) && (!definition.Minimum.HasValue || number >= definition.Minimum) && (!definition.Maximum.HasValue || number <= definition.Maximum),
            _ => false
        };
        if (!valid)
        {
            throw Invalid("插件设置类型或范围不匹配：" + definition.Id);
        }
    }
    private static bool TryGetInt32Safe(this JsonElement value, out int number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number);
    }
}
