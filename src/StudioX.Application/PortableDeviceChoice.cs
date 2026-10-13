namespace StudioX.Application;

public sealed record PortableDeviceChoice(string Id, string DisplayName, IReadOnlyList<PortableTemplateChoice> Templates)
{
    public override string ToString() => DisplayName;
}
