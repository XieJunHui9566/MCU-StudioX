namespace StudioX.Application;

public sealed record GitHubPullRequestFile(string Path, string Status, int Additions, int Deletions, string? Patch);
