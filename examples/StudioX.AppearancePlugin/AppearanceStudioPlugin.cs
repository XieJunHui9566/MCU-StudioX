namespace StudioX.AppearancePlugin;

using System.Text.Json;
using StudioX.Extensions.Abstractions;

/// <summary>提供应用级外观目录；插件不读写偏好，不加载字体或控制背景播放。</summary>
public sealed class AppearanceStudioPlugin : IStudioXPlugin
{
    public PluginContribution Describe() => new([], [new("appearance", "外观工作室", [
        new("style", "appearance", "主题、插画与字体", JsonSerializer.SerializeToElement(new PluginAppearanceDefinition(2, [
            Theme("ocean", "深海蓝", "#14202E", "#1C2B3D", "#293D53", "#3A506A", "#E0ECFA", "#9BB1CB", "#62B5F5", "#102030"),
            Theme("forest", "森林绿", "#18241F", "#22322B", "#30483A", "#46634F", "#DFEEE3", "#A2B8AA", "#83CAA0", "#12261A"),
            Theme("plum", "暮色紫", "#241E2E", "#31283F", "#443752", "#59476D", "#EEE4F7", "#B6A7C8", "#BA91EA", "#26163B"),
            Theme("graphite", "石墨灰", "#202224", "#2C2F32", "#3A3E42", "#50565C", "#E5E8EB", "#A8AFB6", "#84BBCF", "#102B34"),
            Theme("paper", "清爽白", "#FFFFFF", "#F3F6FA", "#E5ECF5", "#CAD5E4", "#233044", "#5F7086", "#2867C7", "#FFFFFF"),
            Theme("cream", "暖纸色", "#FBF6EA", "#F1E8D6", "#E6D9C0", "#CEBDA0", "#41382B", "#796A56", "#9D6634", "#FFFFFF"),
            Theme("hina-night", "日奈 · 紫夜", "#171525", "#242037", "#362D4C", "#564566", "#F1EAFB", "#B7A9CC", "#C5A1EF", "#21172F") with
            {
                Artwork = Hina(.95)
            },
            Theme("hina-moon", "日奈 · 月白", "#F4F0FC", "#E9E2F3", "#DED1EF", "#BEACD3", "#342541", "#756382", "#7E4FAC", "#FFFFFF") with
            {
                Artwork = Hina(.65)
            },
            Theme("hina-dusk", "日奈 · 暮光", "#26212D", "#332A3C", "#47354C", "#665169", "#F7EBE6", "#C8ADBA", "#E2BA81", "#302116") with
            {
                Artwork = Hina(.85)
            }])))
    ])], []);

    public Task ActivateAsync(IPluginHost host, CancellationToken token) => Task.CompletedTask;

    public Task<JsonElement> InvokeAsync(string kind, string id, JsonElement arguments, CancellationToken token) =>
        throw new InvalidOperationException("外观操作由宿主面板处理，此插件不提供可执行命令。");

    public Task DeactivateAsync(CancellationToken token) => Task.CompletedTask;

    private static PluginAppearanceArtwork Hina(double opacity) => new("assets/hina-environment.png",
        "c9668fa0c80201a5d5edbb8b06316795b056e46547f2ff44c8697744904c4399", opacity);

    private static PluginAppearanceTheme Theme(string id, string name, string background, string surface, string panel,
        string border, string text, string muted, string accent, string onAccent) => new("studiox.appearance." + id, name, new()
        {
            ["Background"] = background,
            ["Surface"] = surface,
            ["Panel"] = panel,
            ["Border"] = border,
            ["Text"] = text,
            ["Muted"] = muted,
            ["Accent"] = accent,
            ["OnAccent"] = onAccent
        });
}
