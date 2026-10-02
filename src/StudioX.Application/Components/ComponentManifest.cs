namespace StudioX.Application.Components;

public sealed record ComponentManifest(int FormatVersion, string Id, string Version, string Name, string Description,
    string License, string SourceUrl, string[] Frameworks, string[] Sources, string[] IncludeDirectories, Dictionary<string, string> Sha256);
