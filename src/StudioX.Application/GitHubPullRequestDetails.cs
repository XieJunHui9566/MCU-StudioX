namespace StudioX.Application;

public sealed record GitHubPullRequestDetails(GitHubPullRequest PullRequest,
    IReadOnlyList<GitHubPullRequestFile> Files,
    IReadOnlyList<GitHubPullRequestReview> Reviews,
    IReadOnlyList<GitHubPullRequestComment> Comments,
    IReadOnlyList<GitHubPullRequestReviewComment> ReviewComments,
    GitHubPullRequestChecks Checks);
