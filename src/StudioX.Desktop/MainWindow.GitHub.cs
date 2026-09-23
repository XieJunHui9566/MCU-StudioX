namespace StudioX.Desktop;

using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media.Imaging;
using StudioX.Application;
using StudioX.Foundation;

public partial class MainWindow
{
    private CancellationTokenSource? githubProfileCancellation;
    private int githubProfileVersion;

    private void GitHubSelectedAccountChanged(string? account)
    {
        var version = ++githubProfileVersion;
        githubProfileCancellation?.Cancel();
        githubProfileCancellation = null;
        GitHubAvatarBrush.ImageSource = null;
        GitHubAvatarImage.Visibility = Visibility.Collapsed;
        GitHubIdentityName.Text = account ?? "未登录 GitHub";
        GitHubAvatarInitial.Text = account is { Length: > 0 } ? account[..1].ToUpperInvariant() : "G";
        GitHubAvatarContainer.Visibility = account is null ? Visibility.Collapsed : Visibility.Visible;
        GitHubIdentityButton.ToolTip = account is null ? "未登录 GitHub · 点击打开协作页登录" : $"GitHub：{account} · 打开协作页";
        AutomationProperties.SetName(GitHubIdentityButton, account is null ? "未登录 GitHub" : $"GitHub 账号 {account}");
        if (account is null) return;

        var cancellation = new CancellationTokenSource();
        githubProfileCancellation = cancellation;
        _ = LoadGitHubProfileAsync(account, version, cancellation);
    }

    private async Task LoadGitHubProfileAsync(string account, int version, CancellationTokenSource cancellation)
    {
        try
        {
            var profile = await services.GitHubAccounts.GetProfileAsync(account, cancellation.Token);
            if (cancellation.IsCancellationRequested || version != githubProfileVersion || closed ||
                !string.Equals(account, GitHubWorkspace.SelectedAccount, StringComparison.OrdinalIgnoreCase)) return;

            GitHubIdentityName.Text = profile.Login;
            AutomationProperties.SetName(GitHubIdentityButton, $"GitHub 账号 {profile.Login}");
            GitHubIdentityButton.ToolTip = string.IsNullOrWhiteSpace(profile.Name)
                ? $"GitHub：{profile.Login} · 打开协作页"
                : $"{profile.Name} (@{profile.Login}) · 打开 GitHub 协作页";
            if (profile.AvatarBytes is { Length: > 0 } avatar)
            {
                using var stream = new MemoryStream(avatar);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 48;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                GitHubAvatarBrush.ImageSource = bitmap;
                GitHubAvatarImage.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is GitHubApiException { StatusCode: HttpStatusCode.Unauthorized }
            or StudioXException { Code: "GITHUB_AUTH_REQUIRED" })
        {
            if (version != githubProfileVersion || closed) return;
            GitHubAvatarBrush.ImageSource = null;
            GitHubAvatarImage.Visibility = Visibility.Collapsed;
            GitHubAvatarInitial.Text = "!";
            GitHubIdentityName.Text = "需重新登录";
            GitHubIdentityButton.ToolTip = $"GitHub 账号 {account} 授权已失效 · 点击打开协作页重新登录";
            AutomationProperties.SetName(GitHubIdentityButton, $"GitHub 账号 {account} 需要重新登录");
            GitHubWorkspace.ShowAccountNeedsLogin(account);
            Log("GitHub 账号 " + account + " 需要重新登录：" + ex.Message);
        }
        catch (Exception ex)
        {
            // 头像或资料不可用时仍显示已选择的账号；下次打开协作页可重试。
            if (version == githubProfileVersion && !closed)
                Log("读取 GitHub 用户资料失败：" + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(githubProfileCancellation, cancellation)) githubProfileCancellation = null;
            cancellation.Dispose();
        }
    }

    private void CancelGitHubProfileRefresh()
    {
        githubProfileVersion++;
        githubProfileCancellation?.Cancel();
        githubProfileCancellation = null;
    }

    private async void GitHubWorkspace_Click(object sender, RoutedEventArgs e)
    {
        ShowDocument(GitHubWorkspaceTab);
        await GitHubWorkspace.EnsureLoadedAsync();
    }

    private void GitHubRepositoryValidated(string directory)
    {
        if (!string.Equals(GitGraph.RepositoryDirectory, directory, StringComparison.OrdinalIgnoreCase))
            GitGraph.SetProject(directory);
    }

    private Task<bool> PrepareGitHubWorkingTreeChangeAsync(CancellationToken token) =>
        string.Equals(projectDirectory, GitHubWorkspace.RepositoryDirectory, StringComparison.OrdinalIgnoreCase)
            ? PrepareGitWorkingTreeChangeAsync(token)
            : Task.FromResult(true);

    private Task ApplyGitHubWorkingTreeChangeAsync(CancellationToken token) =>
        string.Equals(projectDirectory, GitHubWorkspace.RepositoryDirectory, StringComparison.OrdinalIgnoreCase)
            ? ApplyGitWorkingTreeChangeAsync(token)
            : Task.CompletedTask;

    private async Task OpenClonedGitHubRepositoryAsync(string directory, CancellationToken token)
    {
        if (File.Exists(Path.Combine(directory, ".studiox", "project.json")))
        {
            await OpenProjectAsync(directory, token);
            if (string.Equals(projectDirectory, directory, StringComparison.OrdinalIgnoreCase))
            {
                ShowDocument(GitHubWorkspaceTab);
                GitHubWorkspace.SetProject(directory);
            }
            else
                Status.Text = "仓库已克隆，打开工程已取消；可稍后从文件菜单打开。";
            return;
        }
        GitHubWorkspace.SetProject(directory);
    }
}
