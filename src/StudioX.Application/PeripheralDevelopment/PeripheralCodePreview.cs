namespace StudioX.Application.PeripheralDevelopment;

public sealed record PeripheralCodePreview(string InstanceName, string Code, string Dependencies, string Guidance,
    IReadOnlyList<string> RequiredComponents);
