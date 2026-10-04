namespace StudioX.Application.PeripheralDevelopment;

public sealed record PeripheralParameter(string Id, string Label, string DefaultValue, int? Minimum = null, int? Maximum = null);
