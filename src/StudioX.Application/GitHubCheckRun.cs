namespace StudioX.Application;

public sealed record GitHubCheckRun(string Name, string Status, string? Conclusion, Uri? HtmlUrl);
