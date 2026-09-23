namespace StudioX.Packages;

/// <summary>StudioX 独立 Pack 格式 1，不与历史同扩展名格式隐式兼容。</summary>
public sealed record PackManifest(int FormatVersion, string Id, string Version, string DisplayName,
    string Vendor, IReadOnlyList<DeviceDefinition> Devices);
