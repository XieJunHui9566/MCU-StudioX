namespace StudioX.Application;

public sealed record ProjectEntry(string Name, string RelativePath, bool IsDirectory, bool IsLink);
