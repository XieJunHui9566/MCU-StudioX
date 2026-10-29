namespace StudioX.Extensions;

/// <summary>插件支持的准确工程组合；各行独立匹配，不展开型号或版本范围。</summary>
public sealed record PluginProjectSupport(string PackId, string PackVersion, string DeviceId, string TemplateId,
    string PackageId, string ProfileId, string ProfileVersion);
