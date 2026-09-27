namespace StudioX.Application;

public sealed record GitHubCreatePullRequest(string Title, string Body, string Head, string Base, bool Draft = false);
