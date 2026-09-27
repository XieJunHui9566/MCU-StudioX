namespace StudioX.Engine;

public sealed record GitCommit(string Hash, IReadOnlyList<string> Parents, string Subject, string Author, DateTimeOffset AuthoredAt);
