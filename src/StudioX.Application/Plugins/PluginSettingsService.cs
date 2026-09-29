namespace StudioX.Application.Plugins;

using System.Text.Json;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

/// <summary>用户设置与插件安装目录分开，升级插件不会覆盖用户选择。</summary>
public sealed class PluginSettingsService(string dataDirectory)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<IReadOnlyDictionary<string, JsonElement>> ReadAsync(string id, IReadOnlyList<PluginSettingDefinition> definitions, CancellationToken token = default)
    {
        var path = SettingsPath(id);
        var saved = File.Exists(path) ? await JsonStore.ReadAsync<Dictionary<string, JsonElement>>(path, token) : [];
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            var value = saved.TryGetValue(definition.Id, out var existing) ? existing : definition.Default;
            PluginContributionValidator.ValidateSetting(definition, value);
            result.Add(definition.Id, value.Clone());
        }
        return result;
    }
    public async Task SaveAsync(string id, IReadOnlyList<PluginSettingDefinition> definitions, IReadOnlyDictionary<string, JsonElement> values, CancellationToken token = default)
    {
        if (values.Count != definitions.Count || definitions.Any(d => !values.ContainsKey(d.Id)))
        {
            throw new StudioXException("PLUGIN_SETTINGS", "设置字段与插件声明不匹配。");
        }
        foreach (var definition in definitions)
        {
            PluginContributionValidator.ValidateSetting(definition, values[definition.Id]);
        }
        await gate.WaitAsync(token);
        try
        {
            await JsonStore.WriteAsync(SettingsPath(id), values, token);
        }
        finally { gate.Release(); }
    }
    private string SettingsPath(string id)
    {
        PluginContributionValidator.Identifier(id);
        return Path.Combine(dataDirectory, "plugin-settings", id + ".json");
    }
}
