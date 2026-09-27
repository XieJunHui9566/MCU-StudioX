namespace StudioX.Application;

/// <summary>PR 统一差异中的逐行审阅评论；过期或文件级评论的 Line 可以为空。</summary>
public sealed record GitHubPullRequestReviewComment(long Id, string Author, string Path,
    int? Line, string? Side, int? OriginalLine, string Body, DateTimeOffset CreatedAt,
    Uri? HtmlUrl, long? ReplyToId);
