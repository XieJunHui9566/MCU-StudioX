namespace StudioX.Application;

public sealed record GitHubPullRequestReview(string Author, string State, string Body, DateTimeOffset? SubmittedAt);
