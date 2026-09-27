namespace StudioX.Application;

public sealed record GitHubPullRequestChecks(string CommitStatus, IReadOnlyList<GitHubCheckRun> Runs, string? UnavailableReason);
