namespace StudioX.Application;

public sealed record ThemeDefinition(int FormatVersion, string Id, string DisplayName, Dictionary<string, string> Colors);
