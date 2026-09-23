namespace StudioX.Application;

using StudioX.Engine;

/// <summary>工作台的图形 Git 入口；Desktop 不直接执行 Git 命令。</summary>
public sealed class GitGraphService(GitRepositoryService repository)
{
    public Task InitializeAsync(string directory, CancellationToken token = default) =>
        repository.InitializeAsync(directory, token);

    public Task<string> CloneAsync(string url, string destinationDirectory, CancellationToken token = default) =>
        repository.CloneAsync(url, destinationDirectory, token);

    public Task<string> CloneAsync(string url, string destinationDirectory, string? selectedAccount,
        CancellationToken token = default) => repository.CloneAsync(url, destinationDirectory, selectedAccount, token);

    public Task<IReadOnlyList<GitRemoteInfo>> GetRemoteDetailsAsync(string directory, CancellationToken token = default) =>
        repository.GetRemoteDetailsAsync(directory, token);

    public Task SetRemoteUrlAsync(string directory, string name, string url, CancellationToken token = default) =>
        repository.SetRemoteUrlAsync(directory, name, url, token);

    public Task SetRemotePushUrlAsync(string directory, string remote, string url, CancellationToken token = default) =>
        repository.SetRemotePushUrlAsync(directory, remote, url, token);

    public Task<IReadOnlyList<GitRemoteBranch>> GetRemoteBranchesAsync(string directory, string? remote = null,
        CancellationToken token = default) => repository.GetRemoteBranchesAsync(directory, remote, token);

    public Task CheckoutRemoteBranchAsync(string directory, string remote, string branch,
        string? localBranch = null, CancellationToken token = default) =>
        repository.CheckoutRemoteBranchAsync(directory, remote, branch, localBranch, token);

    public Task SetUpstreamAsync(string directory, string remote, string localBranch,
        string remoteBranch, CancellationToken token = default) =>
        repository.SetUpstreamAsync(directory, remote, localBranch, remoteBranch, token);

    public Task<GitGraphSnapshot> GetSnapshotAsync(string directory, int maxCommits = 200, CancellationToken token = default) =>
        repository.GetSnapshotAsync(directory, maxCommits, token);

    public Task<GitCommitDetails> GetCommitDetailsAsync(string directory, string hash, CancellationToken token = default) =>
        repository.GetCommitDetailsAsync(directory, hash, token);

    public Task<GitDiffResult> GetDiffAsync(string directory, GitDiffTarget target, string? path = null,
        string? commitHash = null, CancellationToken token = default) =>
        repository.GetDiffAsync(directory, target, path, commitHash, token);

    public Task<GitDiffResult> CompareCommitsAsync(string directory, string fromHash, string toHash,
        string? path = null, CancellationToken token = default) =>
        repository.CompareCommitsAsync(directory, fromHash, toHash, path, token);

    public Task StageAsync(string directory, IReadOnlyList<string> paths, CancellationToken token = default) =>
        repository.StageAsync(directory, paths, token);

    public Task UnstageAsync(string directory, IReadOnlyList<string> paths, CancellationToken token = default) =>
        repository.UnstageAsync(directory, paths, token);

    public Task CommitAsync(string directory, string message, CancellationToken token = default) =>
        repository.CommitAsync(directory, message, token);

    public Task CreateBranchAsync(string directory, string name, string? startPoint = null,
        bool checkout = false, CancellationToken token = default) =>
        repository.CreateBranchAsync(directory, name, startPoint, checkout, token);

    public Task CheckoutBranchAsync(string directory, string name, CancellationToken token = default) =>
        repository.CheckoutBranchAsync(directory, name, token);

    public Task DeleteBranchAsync(string directory, string name, CancellationToken token = default) =>
        repository.DeleteBranchAsync(directory, name, token);

    public Task MergeAsync(string directory, string branchName, CancellationToken token = default) =>
        repository.MergeAsync(directory, branchName, token);

    public Task FetchAsync(string directory, string? remote = null, CancellationToken token = default) =>
        repository.FetchAsync(directory, remote, token);

    public Task FetchAsync(string directory, string? remote, string? selectedAccount,
        CancellationToken token = default) => repository.FetchAsync(directory, remote, selectedAccount, token);

    public Task PullAsync(string directory, CancellationToken token = default) => repository.PullAsync(directory, token);

    public Task PullAsync(string directory, string? selectedAccount, CancellationToken token = default) =>
        repository.PullAsync(directory, selectedAccount, token);

    public Task PushAsync(string directory, CancellationToken token = default) => repository.PushAsync(directory, token);

    public Task PushAsync(string directory, string? selectedAccount, CancellationToken token = default) =>
        repository.PushAsync(directory, selectedAccount, token);

    public Task<GitIdentity> GetLocalIdentityAsync(string directory, CancellationToken token = default) =>
        repository.GetLocalIdentityAsync(directory, token);

    public Task SetLocalIdentityAsync(string directory, string name, string email, CancellationToken token = default) =>
        repository.SetLocalIdentityAsync(directory, name, email, token);

    public Task<IReadOnlyList<string>> GetRemotesAsync(string directory, CancellationToken token = default) =>
        repository.GetRemotesAsync(directory, token);

    public Task AddRemoteAsync(string directory, string name, string url, CancellationToken token = default) =>
        repository.AddRemoteAsync(directory, name, url, token);

    public Task PublishBranchAsync(string directory, string remote, string branch, CancellationToken token = default) =>
        repository.PublishBranchAsync(directory, remote, branch, token);

    public Task PublishBranchAsync(string directory, string remote, string branch, string? selectedAccount,
        CancellationToken token = default) => repository.PublishBranchAsync(directory, remote, branch, selectedAccount, token);
}
