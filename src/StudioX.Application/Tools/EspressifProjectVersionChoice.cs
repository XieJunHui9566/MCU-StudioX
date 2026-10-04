namespace StudioX.Application.Tools;

using StudioX.Packages;

public sealed record EspressifProjectVersionChoice(string SdkVersion, string ComponentVersion, InstalledPack? Pack,
    string DeviceId, string TemplateId, EspressifVersionState State, string Message)
{
    public bool CanCreate => Pack is not null && State is EspressifVersionState.Ready or EspressifVersionState.Missing;
    public string DisplayName => $"ESP-IDF {SdkVersion} · 组件 {ComponentVersion} · " + (State switch
    {
        EspressifVersionState.Ready => "已安装",
        EspressifVersionState.Missing => "组件未安装",
        EspressifVersionState.Disabled => "已禁用",
        EspressifVersionState.PackMissing => "缺少匹配器件包",
        _ => "需要修复"
    });
}
