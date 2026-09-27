namespace StudioX.Engine;

public sealed record GitChangedFile(string Path, string? OriginalPath, string Status);
