namespace StudioX.Desktop;

using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

/// <summary>GitHub 协作界面；凭据始终由 GCM 管理，界面只接收账号名。</summary>
public partial class GitHubWorkspaceView : UserControl
{
    private GitHubAccountService? accountService;
    private GitGraphService? gitService;
    private GitHubPullRequestService? pullRequestService;
    private string? directory;
    private GitGraphSnapshot? repositorySnapshot;
    private IReadOnlyList<GitRemoteInfo> remotes = [];
    private IReadOnlyList<GitRemoteBranch> remoteBranches = [];
    private CancellationTokenSource? activeAction;
    private bool busy;
    private bool changingRemote;
    private bool changingAccount;
    private bool hasLocalRepository;
    private int projectVersion;
    private string? lastNotifiedAccount;

    public string? RepositoryDirectory => directory;
    public event Action<string>? RepositoryValidated;
    public string? SelectedAccount => AccountPicker.SelectedItem as string;
    public event Action<string?>? SelectedAccountChanged;

    public void ShowAccountNeedsLogin(string account)
    {
        if (string.Equals(SelectedAccount, account, StringComparison.OrdinalIgnoreCase))
            AccountStatus.Text = "授权已失效，请重新登录";
    }

    public Func<string, CancellationToken, Task>? OpenClonedRepositoryAsync { get; set; }
    public Func<CancellationToken, Task<bool>>? BeforeWorkingTreeChangeAsync { get; set; }
    public Func<CancellationToken, Task>? WorkingTreeChangedAsync { get; set; }
    public Action<string>? LogDiagnostic { get; set; }

    public GitHubWorkspaceView()
    {
        InitializeComponent();
        InitializePullRequestControls();
        UpdateActions();
    }

    public void Attach(GitHubAccountService accounts, GitGraphService git, GitHubPullRequestService pullRequests)
    {
        accountService = accounts;
        gitService = git;
        pullRequestService = pullRequests;
    }

    public void SetProject(string? projectDirectory)
    {
        projectVersion++;
        directory = projectDirectory;
        repositorySnapshot = null;
        hasLocalRepository = false;
        remotes = [];
        remoteBranches = [];
        changingRemote = true;
        RemotePicker.ItemsSource = null;
        RemotePicker.Text = "origin";
        RemoteUrlBox.Clear();
        PushUrlBox.Clear();
        changingRemote = false;
        RemoteBranchesList.ItemsSource = null;
        CurrentProjectText.Text = directory is null ? "未打开工程；可先从 GitHub 克隆。" : directory;
        RemoteHint.Text = "设置 GitHub 远端后可获取、发布和同步分支。";
        SetPullRequestRepository(null);
        UpdateActions();
    }

    public async Task EnsureLoadedAsync()
    {
        if (accountService is null || gitService is null) return;
        await RunActionAsync("加载 GitHub 协作", async token =>
        {
            await RefreshAccountsAsync(token);
            NotifySelectedAccountChanged(force: true);
            await RefreshRepositoryAsync(token);
            await RefreshPullRequestsAsync(token);
        });
    }

    public Task RefreshAccountsForChromeAsync(CancellationToken token = default) => RefreshAccountsAsync(token);

    private void NotifySelectedAccountChanged(bool force = false)
    {
        var selected = SelectedAccount;
        if (!force && string.Equals(lastNotifiedAccount, selected, StringComparison.OrdinalIgnoreCase)) return;
        lastNotifiedAccount = selected;
        SelectedAccountChanged?.Invoke(selected);
    }

    private async Task RefreshAccountsAsync(CancellationToken token)
    {
        if (accountService is null) return;
        var previous = AccountPicker.SelectedItem as string;
        var names = await accountService.ListAccountsAsync(token);
        changingAccount = true;
        AccountPicker.ItemsSource = names;
        AccountPicker.SelectedItem = names.FirstOrDefault(name => name == previous) ?? names.FirstOrDefault();
        changingAccount = false;
        if (!string.Equals(previous, AccountPicker.SelectedItem as string, StringComparison.Ordinal))
        { pullRequestVersion++; PrList.ItemsSource = null; ClearPullRequestDetails(); }
        AccountStatus.Text = names.Count == 0 ? "未登录 GitHub" : names.Count == 1 ? "已登录" : $"{names.Count} 个账号";
        LogoutButton.IsEnabled = names.Count > 0 && !busy;
        NotifySelectedAccountChanged();
        UpdateActions();
    }

    private async Task RefreshRepositoryAsync(CancellationToken token)
    {
        if (gitService is null || directory is null)
        {
            CurrentProjectText.Text = "未打开工程；可先从 GitHub 克隆。";
            SetPullRequestRepository(null);
            UpdateActions();
            return;
        }
        var version = projectVersion;
        var current = directory;
        try
        {
            var graph = await gitService.GetSnapshotAsync(current, token: token);
            var details = await gitService.GetRemoteDetailsAsync(current, token);
            var branches = await gitService.GetRemoteBranchesAsync(current, token: token);
            if (version != projectVersion || current != directory) return;
            hasLocalRepository = true;
            repositorySnapshot = graph;
            remotes = details;
            remoteBranches = branches;
            // 只在确认选中的目录是独立 Git 仓库后通知图谱，避免误绑普通目录或上级仓库。
            RepositoryValidated?.Invoke(graph.RepositoryDirectory);
            var branchLabel = graph.DetachedHead ? "游离 HEAD" : graph.CurrentBranch;
            var sync = graph.Upstream is null ? "未设置上游" : $"{graph.Upstream} · 领先 {graph.Ahead} / 落后 {graph.Behind}";
            CurrentProjectText.Text = $"{current}\n当前分支：{branchLabel} · {sync}";
            var previous = RemotePicker.Text.Trim();
            changingRemote = true;
            RemotePicker.ItemsSource = details.Select(remote => remote.Name).ToArray();
            RemotePicker.Text = details.FirstOrDefault(remote => remote.Name == previous)?.Name ??
                                details.FirstOrDefault(remote => remote.Name == "origin")?.Name ??
                                details.FirstOrDefault()?.Name ?? "origin";
            changingRemote = false;
            UpdateSelectedRemote();
            RemoteBranchesList.ItemsSource = branches.Select(branch => new RemoteBranchRow(branch)).ToArray();
            UpdatePullRequestRepositoryFromRemote();
            WorkspaceStatus.Text = "仓库状态已更新。";
        }
        catch (StudioXException ex) when (ex.Code is "GIT_NOT_REPOSITORY" or "GIT_NOT_PROJECT_REPOSITORY")
        {
            if (version != projectVersion) return;
            hasLocalRepository = false;
            repositorySnapshot = null;
            remotes = [];
            remoteBranches = [];
            RemoteBranchesList.ItemsSource = null;
            CurrentProjectText.Text = ex.Code == "GIT_NOT_PROJECT_REPOSITORY"
                ? "当前工程位于上级 Git 仓库内。请单独克隆或选择仓库根目录。"
                : current + "\n尚未初始化 Git 仓库。";
            RemoteHint.Text = "初始化 Git 后，可连接 GitHub 远端。";
            SetPullRequestRepository(null);
            WorkspaceStatus.Text = CurrentProjectText.Text;
        }
        finally { if (version == projectVersion) UpdateActions(); }
    }

    private void UpdateSelectedRemote()
    {
        if (changingRemote) return;
        var selected = remotes.FirstOrDefault(remote => remote.Name == RemotePicker.Text.Trim());
        if (selected is null)
        {
            RemoteUrlBox.Clear();
            PushUrlBox.Clear();
            RemoteHint.Text = "输入新的远端名称和 GitHub URL，然后保存。";
            return;
        }
        RemoteUrlBox.Text = selected.FetchUrl;
        PushUrlBox.Text = selected.PushUrl;
        RemoteHint.Text = selected.HasEmbeddedCredentials
            ? "该远端的仓库地址或推送地址含有凭据。请把两处分别改为无凭据的 HTTPS 或 SSH 地址并保存。"
            : selected.IsGitHub ? "GitHub 远端已连接。" : "这是非 GitHub 远端；Git 操作仍可用，Pull Request 仅支持 GitHub。";
    }

    private void UpdateActions()
    {
        var ready = !busy;
        var branch = repositorySnapshot?.CurrentBranch;
        var validBranch = hasLocalRepository && repositorySnapshot is { DetachedHead: false, HeadCommit: not null } &&
                          !string.IsNullOrWhiteSpace(branch);
        var remoteName = RemotePicker.Text.Trim();
        var hasRemote = remotes.Any(remote => remote.Name == remoteName && !remote.HasEmbeddedCredentials);
        var upstreamRemote = repositorySnapshot?.Upstream?.Split('/', 2)[0];
        var selectedUpstream = hasRemote && string.Equals(remoteName, upstreamRemote, StringComparison.Ordinal);
        LoginButton.IsEnabled = ready && accountService is not null;
        AccountPicker.IsEnabled = ready;
        RemotePicker.IsEnabled = ready;
        RemoteBranchesList.IsEnabled = ready;
        PrStatePicker.IsEnabled = ready;
        LogoutButton.IsEnabled = ready && AccountPicker.SelectedItem is string;
        CancelActionButton.IsEnabled = busy;
        CloneButton.IsEnabled = ready && gitService is not null;
        InitializeGitButton.IsEnabled = ready && directory is not null && !hasLocalRepository;
        SaveRemoteButton.IsEnabled = ready && hasLocalRepository;
        SavePushUrlButton.IsEnabled = ready && hasLocalRepository &&
            remotes.Any(remote => remote.Name == remoteName);
        FetchButton.IsEnabled = ready && hasRemote;
        PullButton.IsEnabled = ready && selectedUpstream;
        PushButton.IsEnabled = ready && selectedUpstream;
        PullButton.Content = repositorySnapshot?.Upstream is { } pullTarget ? $"快进拉取 {pullTarget}" : "快进拉取";
        PushButton.Content = repositorySnapshot?.Upstream is { } pushTarget ? $"推送到 {pushTarget}" : "推送当前分支";
        PublishButton.IsEnabled = ready && hasRemote && validBranch;
        CheckoutRemoteButton.IsEnabled = ready && hasLocalRepository && RemoteBranchesList.SelectedItem is RemoteBranchRow;
        SetUpstreamButton.IsEnabled = ready && validBranch && RemoteBranchesList.SelectedItem is RemoteBranchRow;
        UpdatePullRequestActions();
    }

    private async Task RunActionAsync(string label, Func<CancellationToken, Task> action)
    {
        if (busy) return;
        busy = true;
        activeAction = new CancellationTokenSource();
        var pendingMessage = label + "…";
        WorkspaceStatus.Text = pendingMessage;
        UpdateActions();
        try
        {
            await action(activeAction.Token);
            if (!activeAction.IsCancellationRequested && WorkspaceStatus.Text == pendingMessage)
                WorkspaceStatus.Text = label + "完成。";
        }
        catch (OperationCanceledException) when (activeAction.IsCancellationRequested)
        { WorkspaceStatus.Text = label + "已取消。"; }
        catch (Exception ex)
        {
            var message = ex is StudioXException ? ex.Message : label + "失败：" + ex.Message;
            WorkspaceStatus.Text = message;
            LogDiagnostic?.Invoke(message);
            MessageBox.Show(Window.GetWindow(this), message, label, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            activeAction.Dispose();
            activeAction = null;
            busy = false;
            UpdateActions();
        }
    }

    private async Task AfterMutationAsync(string completedMessage, Func<Task> refresh)
    {
        WorkspaceStatus.Text = completedMessage;
        try { await refresh(); }
        catch (OperationCanceledException)
        { WorkspaceStatus.Text = completedMessage + "后续界面刷新已取消，可手动刷新确认。"; }
        catch (Exception ex)
        {
            WorkspaceStatus.Text = completedMessage + "但界面刷新失败，可手动刷新确认：" + ex.Message;
            LogDiagnostic?.Invoke(WorkspaceStatus.Text);
        }
    }

    private async void Login_Click(object sender, RoutedEventArgs e) => await RunActionAsync("登录 GitHub", async token =>
    {
        if (accountService is null) return;
        var known = (AccountPicker.ItemsSource as IEnumerable<string> ?? []).ToArray();
        AccountStatus.Text = "请在浏览器中完成 GitHub 授权…";
        var available = await accountService.LoginWithBrowserAsync(token);
        await RefreshAccountsAsync(token);
        var newAccount = available.Except(known, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (newAccount is not null)
        {
            changingAccount = true;
            AccountPicker.SelectedItem = newAccount;
            changingAccount = false;
            NotifySelectedAccountChanged();
            pullRequestVersion++;
            PrList.ItemsSource = null;
            ClearPullRequestDetails();
        }
        NotifySelectedAccountChanged(force: true);
        await RefreshPullRequestsAsync(token);
    });

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (AccountPicker.SelectedItem is not string account || accountService is null) return;
        if (!Confirm($"从本机安全存储中退出 GitHub 账号 {account}？\n仓库文件和提交不会删除。", "退出 GitHub")) return;
        await RunActionAsync("退出 GitHub", async token =>
        {
            await accountService.LogoutAsync(account, token);
            await RefreshAccountsAsync(token);
            pullRequestVersion++;
            await RefreshPullRequestsAsync(token);
        });
    }

    private void CancelAction_Click(object sender, RoutedEventArgs e) => activeAction?.Cancel();

    private void ChooseCloneDestination_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择克隆仓库的上级目录" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var name = GuessRepositoryName(CloneUrlBox.Text);
        CloneDestinationBox.Text = Path.Combine(dialog.FolderName, name);
    }

    private async void ChooseLocalRepository_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var dialog = new OpenFolderDialog { Title = "选择本地 Git 仓库或工程根目录" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        SetProject(dialog.FolderName);
        await RunActionAsync("打开本地仓库", RefreshRepositoryAsync);
    }

    private static string GuessRepositoryName(string url)
    {
        var tail = url.Trim().TrimEnd('/').Split('/', ':').LastOrDefault() ?? "";
        if (tail.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) tail = tail[..^4];
        return tail.Length > 0 && tail.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 ? tail : "github-project";
    }

    private async void Clone_Click(object sender, RoutedEventArgs e) => await RunActionAsync("克隆仓库", async token =>
    {
        if (gitService is null) return;
        var startingVersion = projectVersion;
        var url = CloneUrlBox.Text.Trim();
        var destination = CloneDestinationBox.Text.Trim();
        if (url.Length == 0 || destination.Length == 0)
            throw new StudioXException("GIT_CLONE_INPUT", "请填写仓库地址和目标目录。");
        var cloned = await gitService.CloneAsync(url, destination, AccountPicker.SelectedItem as string, token);
        await AfterMutationAsync($"仓库已克隆到 {cloned}。", async () =>
        {
            if (projectVersion != startingVersion)
            { WorkspaceStatus.Text = $"仓库已克隆：{cloned}。当前工程已切换，可稍后打开克隆目录。"; return; }
            if (OpenClonedRepositoryAsync is not null) await OpenClonedRepositoryAsync(cloned, token);
            else SetProject(cloned);
            if (string.Equals(RepositoryDirectory, cloned, StringComparison.OrdinalIgnoreCase))
            {
                await RefreshRepositoryAsync(token);
                WorkspaceStatus.Text = File.Exists(Path.Combine(cloned, ".studiox", "project.json"))
                    ? $"仓库已克隆并打开：{cloned}。"
                    : $"仓库已克隆：{cloned}。该目录没有 MCU StudioX 工程清单，仅在协作页管理 Git。";
            }
            else WorkspaceStatus.Text = $"仓库已克隆：{cloned}。IDE 工程未切换，可稍后打开。";
        });
    });

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunActionAsync("刷新 GitHub 协作", async token =>
    {
        await RefreshAccountsAsync(token);
        await RefreshRepositoryAsync(token);
        await RefreshPullRequestsAsync(token);
    });

    private async void InitializeGit_Click(object sender, RoutedEventArgs e)
    {
        if (directory is null || gitService is null) return;
        if (!Confirm("在当前工程目录创建本地 Git 仓库？此操作不会生成提交或推送到 GitHub。", "初始化 Git")) return;
        await RunActionAsync("初始化 Git", async token =>
        {
            await gitService.InitializeAsync(directory, token);
            await RefreshRepositoryAsync(token);
        });
    }

    private async void Remote_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (changingRemote) return;
        UpdateSelectedRemote();
        UpdatePullRequestRepositoryFromRemote();
        UpdateActions();
        await RunActionAsync("切换远端仓库", RefreshPullRequestsAsync);
    }

    private void RemoteBranch_Changed(object sender, SelectionChangedEventArgs e) => UpdateActions();

    private async void SaveRemote_Click(object sender, RoutedEventArgs e) => await RunActionAsync("保存远端", async token =>
    {
        if (gitService is null || directory is null) return;
        var name = RemotePicker.Text.Trim();
        var url = RemoteUrlBox.Text.Trim();
        if (name.Length == 0 || url.Length == 0)
            throw new StudioXException("GIT_REMOTE_INPUT", "请填写远端名称和仓库地址。");
        var existing = remotes.FirstOrDefault(remote => remote.Name == name);
        if (existing is null) await gitService.AddRemoteAsync(directory, name, url, token);
        else
        {
            if (!Confirm($"修改远端 {name} 的地址？后续推送将使用新地址。", "修改远端"))
            { WorkspaceStatus.Text = "已取消修改远端。"; return; }
            await gitService.SetRemoteUrlAsync(directory, name, url, token);
        }
        await AfterMutationAsync($"远端 {name} 已保存。", () => RefreshRepositoryAsync(token));
    });

    private async void SavePushUrl_Click(object sender, RoutedEventArgs e)
    {
        if (gitService is null || directory is null) return;
        var remote = RemotePicker.Text.Trim();
        var url = PushUrlBox.Text.Trim();
        if (!Confirm($"将远端 {remote} 的推送地址设为以下地址？\n{url}\n后续推送会写入这个仓库。", "保存推送地址")) return;
        await RunActionAsync("保存推送地址", async token =>
        {
            await gitService.SetRemotePushUrlAsync(directory, remote, url, token);
            await AfterMutationAsync("推送地址已保存。", () => RefreshRepositoryAsync(token));
        });
    }

    private async void Fetch_Click(object sender, RoutedEventArgs e) => await RunActionAsync("获取远端更新", async token =>
    {
        if (gitService is null || directory is null) return;
        await gitService.FetchAsync(directory, RemotePicker.Text.Trim(), AccountPicker.SelectedItem as string, token);
        await AfterMutationAsync("远端更新已获取。", () => RefreshRepositoryAsync(token));
    });

    private async void Pull_Click(object sender, RoutedEventArgs e) => await RunActionAsync("快进拉取", async token =>
    {
        if (gitService is null || directory is null) return;
        var target = directory;
        var version = projectVersion;
        if (BeforeWorkingTreeChangeAsync is not null && !await BeforeWorkingTreeChangeAsync(token))
        { WorkspaceStatus.Text = "已取消快进拉取。"; return; }
        if (version != projectVersion || directory != target)
        { WorkspaceStatus.Text = "工程已切换，快进拉取已取消。"; return; }
        await gitService.PullAsync(target, AccountPicker.SelectedItem as string, token);
        await AfterMutationAsync("快进拉取已完成。", async () =>
        {
            if (version != projectVersion || directory != target) return;
            if (WorkingTreeChangedAsync is not null) await WorkingTreeChangedAsync(token);
            await RefreshRepositoryAsync(token);
        });
    });

    private async void Push_Click(object sender, RoutedEventArgs e) => await RunActionAsync("推送当前分支", async token =>
    {
        if (gitService is null || directory is null) return;
        await gitService.PushAsync(directory, AccountPicker.SelectedItem as string, token);
        await AfterMutationAsync("当前分支已推送。", () => RefreshRepositoryAsync(token));
    });

    private async void Publish_Click(object sender, RoutedEventArgs e)
    {
        if (gitService is null || directory is null || repositorySnapshot is not { DetachedHead: false, HeadCommit: not null } snapshot) return;
        var remote = RemotePicker.Text.Trim();
        if (!Confirm($"将当前分支 {snapshot.CurrentBranch} 首次发布到 {remote}？", "发布分支")) return;
        await RunActionAsync("发布当前分支", async token =>
        {
            await gitService.PublishBranchAsync(directory, remote, snapshot.CurrentBranch,
                AccountPicker.SelectedItem as string, token);
            await AfterMutationAsync($"分支已发布到 {remote}。", () => RefreshRepositoryAsync(token));
        });
    }

    private async void RefreshBranches_Click(object sender, RoutedEventArgs e) => await RunActionAsync("刷新远端分支", RefreshRepositoryAsync);

    private async void CheckoutRemote_Click(object sender, RoutedEventArgs e)
    {
        if (directory is null || gitService is null || RemoteBranchesList.SelectedItem is not RemoteBranchRow selected) return;
        var target = directory;
        var version = projectVersion;
        if (!Confirm($"从 {selected.Branch.Remote}/{selected.Branch.Name} 创建并检出本地分支？\n工作区文件将切换到该分支。", "检出远端分支")) return;
        await RunActionAsync("检出远端分支", async token =>
        {
            if (BeforeWorkingTreeChangeAsync is not null && !await BeforeWorkingTreeChangeAsync(token))
            { WorkspaceStatus.Text = "已取消检出远端分支。"; return; }
            if (version != projectVersion || directory != target)
            { WorkspaceStatus.Text = "工程已切换，检出远端分支已取消。"; return; }
            await gitService.CheckoutRemoteBranchAsync(target, selected.Branch.Remote, selected.Branch.Name, token: token);
            await AfterMutationAsync("远端分支已检出。", async () =>
            {
                if (version != projectVersion || directory != target) return;
                if (WorkingTreeChangedAsync is not null) await WorkingTreeChangedAsync(token);
                await RefreshRepositoryAsync(token);
            });
        });
    }

    private async void SetUpstream_Click(object sender, RoutedEventArgs e)
    {
        if (directory is null || gitService is null || repositorySnapshot is not { DetachedHead: false } snapshot ||
            RemoteBranchesList.SelectedItem is not RemoteBranchRow selected) return;
        if (!Confirm($"把本地 {snapshot.CurrentBranch} 的上游设为 {selected.Branch.Remote}/{selected.Branch.Name}？", "设置上游")) return;
        await RunActionAsync("设置上游", async token =>
        {
            await gitService.SetUpstreamAsync(directory, selected.Branch.Remote, snapshot.CurrentBranch, selected.Branch.Name, token);
            await AfterMutationAsync("上游分支已设置。", () => RefreshRepositoryAsync(token));
        });
    }

    private bool Confirm(string message, string title) =>
        MessageBox.Show(Window.GetWindow(this), message, title, MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    private sealed record RemoteBranchRow(GitRemoteBranch Branch)
    {
        public string Display => Branch.Remote + "/" + Branch.Name + (Branch.IsUpstream ? " · 当前上游" : "");
    }
}
