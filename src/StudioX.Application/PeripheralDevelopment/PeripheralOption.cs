namespace StudioX.Application.PeripheralDevelopment;

public sealed record PeripheralOption(string Id, string Name, string Header, string Component, bool Available,
    string Availability, IReadOnlyList<PeripheralParameter> Parameters, string Notes, string DocumentationUrl);
