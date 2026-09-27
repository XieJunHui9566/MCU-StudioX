namespace StudioX.Engine;

public sealed record GitCommitDetails(string Hash, string Subject, string Message, string Author, string AuthorEmail,
    DateTimeOffset AuthoredAt, string Committer, string CommitterEmail, DateTimeOffset CommittedAt,
    IReadOnlyList<GitChangedFile> Files);
