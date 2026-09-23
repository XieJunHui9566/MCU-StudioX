namespace StudioX.Application;

using System.Net;

/// <summary>仅支持 github.com；不要从任意 Git 远端推断 API 主机。</summary>
public sealed record GitHubRepository(string Owner, string Name)
{
    public string FullName => $"{Owner}/{Name}";
    public Uri WebUrl => new($"https://github.com/{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(Name)}");
}

public enum GitHubPullRequestState { Open, Closed, All }
public enum GitHubReviewEvent { Comment, Approve, RequestChanges }
public enum GitHubMergeMethod { Merge, Squash, Rebase }

public sealed record GitHubCreatePullRequest(string Title, string Body, string Head, string Base, bool Draft = false);

public sealed record GitHubPullRequest(
    int Number, string Title, string Body, string State, bool Draft, bool Merged,
    string Author, string Head, string HeadSha, string Base, string HeadRepository,
    string BaseRepository, Uri HtmlUrl, DateTimeOffset UpdatedAt, bool? Mergeable,
    string? MergeableState);

public sealed record GitHubPullRequestFile(string Path, string Status, int Additions, int Deletions, string? Patch);
public sealed record GitHubPullRequestReview(string Author, string State, string Body, DateTimeOffset? SubmittedAt);
public sealed record GitHubPullRequestComment(string Author, string Body, DateTimeOffset CreatedAt, Uri? HtmlUrl);
/// <summary>PR 统一差异中的逐行审阅评论；过期或文件级评论的 Line 可以为空。</summary>
public sealed record GitHubPullRequestReviewComment(long Id, string Author, string Path,
    int? Line, string? Side, int? OriginalLine, string Body, DateTimeOffset CreatedAt,
    Uri? HtmlUrl, long? ReplyToId);
public sealed record GitHubCheckRun(string Name, string Status, string? Conclusion, Uri? HtmlUrl);
public sealed record GitHubPullRequestChecks(string CommitStatus, IReadOnlyList<GitHubCheckRun> Runs, string? UnavailableReason);
public sealed record GitHubPullRequestDetails(GitHubPullRequest PullRequest,
    IReadOnlyList<GitHubPullRequestFile> Files,
    IReadOnlyList<GitHubPullRequestReview> Reviews,
    IReadOnlyList<GitHubPullRequestComment> Comments,
    IReadOnlyList<GitHubPullRequestReviewComment> ReviewComments,
    GitHubPullRequestChecks Checks);
public sealed record GitHubMergeResult(bool Merged, string Sha, string Message);

/// <summary>GitHub 拒绝操作时保留状态码；消息仅由安全的服务端 message 字段生成。</summary>
public sealed class GitHubApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
