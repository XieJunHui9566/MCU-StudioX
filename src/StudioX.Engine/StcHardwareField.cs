namespace StudioX.Engine;

public sealed record StcHardwareField(string Id, string Label, IReadOnlyList<StcHardwareValue> Values);
