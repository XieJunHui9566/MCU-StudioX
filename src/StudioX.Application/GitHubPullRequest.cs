namespace StudioX.Application;

public sealed record GitHubPullRequest(
    int Number, string Title, string Body, string State, bool Draft, bool Merged,
    string Author, string Head, string HeadSha, string Base, string HeadRepository,
    string BaseRepository, Uri HtmlUrl, DateTimeOffset UpdatedAt, bool? Mergeable,
    string? MergeableState);
