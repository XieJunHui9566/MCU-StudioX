namespace StudioX.Engine;

public sealed record Ag32SetupField(string Key, string Label, string Default, string[]? Choices = null,
    int? Minimum = null, int? Maximum = null);
