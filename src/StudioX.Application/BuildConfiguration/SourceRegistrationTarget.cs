namespace StudioX.Application.BuildConfiguration;

public sealed record SourceRegistrationTarget(string Name, IReadOnlyList<string> Sources);
