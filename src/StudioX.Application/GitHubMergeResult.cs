namespace StudioX.Application;

public sealed record GitHubMergeResult(bool Merged, string Sha, string Message);
