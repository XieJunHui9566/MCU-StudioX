namespace StudioX.Application;

public sealed record PortableTemplateChoice(string Id, string DisplayName, string Description, bool HostCompatible)
{
    public override string ToString() => DisplayName + (HostCompatible ? "" : " · 当前平台不支持");
}
