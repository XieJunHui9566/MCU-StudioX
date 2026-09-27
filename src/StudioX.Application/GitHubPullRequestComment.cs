namespace StudioX.Application;

public sealed record GitHubPullRequestComment(string Author, string Body, DateTimeOffset CreatedAt, Uri? HtmlUrl);
