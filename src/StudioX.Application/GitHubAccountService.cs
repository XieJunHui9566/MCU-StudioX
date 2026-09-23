namespace StudioX.Application;

using StudioX.Engine;

/// <summary>工作台的 GitHub 账号入口；令牌从引擎凭据服务读取，不交给桌面视图。</summary>
public sealed class GitHubAccountService(GitHubAuthenticationService authentication, GitHubProfileService profiles)
{
    public Task<IReadOnlyList<string>> ListAccountsAsync(CancellationToken token = default) =>
        authentication.ListAccountsAsync(token);

    public Task<IReadOnlyList<string>> LoginWithBrowserAsync(CancellationToken token = default) =>
        authentication.LoginWithBrowserAsync(token);

    public Task LogoutAsync(string account, CancellationToken token = default) =>
        authentication.LogoutAsync(account, token);

    public Task<GitHubUserProfile> GetProfileAsync(string account, CancellationToken token = default) =>
        profiles.GetProfileAsync(account, token);
}
