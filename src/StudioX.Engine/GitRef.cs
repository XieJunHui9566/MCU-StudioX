namespace StudioX.Engine;

public sealed record GitRef(string Name, string FullName, string Hash, GitRefKind Kind, bool IsCurrent);
