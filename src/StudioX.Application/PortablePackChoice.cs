namespace StudioX.Application;

/// <summary>桌面选择器仅接收目录摘要，安装路径与创建校验留在应用服务。</summary>
public sealed record PortablePackChoice(string Id, string Version, string DisplayName, string Vendor, string ContentHash,
    IReadOnlyList<PortableDeviceChoice> Devices)
{
    public override string ToString() => DisplayName + " / " + Version;
}
