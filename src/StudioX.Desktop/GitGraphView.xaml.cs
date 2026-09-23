namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StudioX.Application;
using StudioX.Engine;

/// <summary>工程内的图形化 Git 入口；所有仓库操作经应用服务使用内置 Git。</summary>
public partial class GitGraphView : UserControl
{
    private GitGraphService? service;
    private string? directory;
    private GitGraphSnapshot? snapshot;
    private CancellationTokenSource projectCancellation = new();
    private int projectVersion;
    private int detailVersion;
    private bool busy;
    private bool changingFilter;
    private bool showingComparison;
    private int maxCommits = 300;
    private GitCommitRow? compareBase;
    private GitCommitDetails? selectedDetails;
    private string? selectedCommitHash;

    public Action<string>? LogDiagnostic { get; set; }
    public string? RepositoryDirectory => directory;
    public Func<Func<CancellationToken, Task>, Task>? ExecuteOperationAsync { get; set; }
    public Action<bool>? MutationStateChanged { get; set; }
    public bool IsMutating { get; private set; }
    public Func<CancellationToken, Task<bool>>? BeforeStageAsync { get; set; }
    public Func<CancellationToken, Task<bool>>? BeforeWorkingTreeChangeAsync { get; set; }
    public Func<CancellationToken, Task>? WorkingTreeChangedAsync { get; set; }

    public GitGraphView()
    {
        InitializeComponent();
        BranchFilter.DisplayMemberPath = nameof(BranchChoice.Display);
        UpdateActions();
    }

    public void Attach(GitGraphService gitGraphService) => service = gitGraphService;

    public void SetProject(string? projectDirectory)
    {
        projectCancellation.Cancel();
        projectCancellation.Dispose();
        projectCancellation = new();
        projectVersion++;
        busy = false;
        detailVersion++;
        directory = projectDirectory;
        snapshot = null;
        maxCommits = 300;
        compareBase = null;
        selectedDetails = null;
        selectedCommitHash = null;
        CommitList.ItemsSource = null;
        WorkingFilesList.ItemsSource = null;
        ChangedFilesList.ItemsSource = null;
        RefsList.ItemsSource = null;
        BranchFilter.ItemsSource = null;
        SearchBox.Clear();
        CommitMessage.Clear();
        DiffBox.Text = projectDirectory is null ? "打开工程后可查看 Git 历史。" : "打开 Git 图谱后读取提交历史。";
        DetailTitle.Text = "选择一条提交查看详情";
        DetailMetadata.Text = "";
        CurrentBranchText.Text = projectDirectory is null ? "未打开工程" : "待读取";
        WorkingSummary.Text = projectDirectory is null ? "打开工程后显示更改" : "待读取";
        GitStatus.Text = projectDirectory is null ? "请先打开工程" : "准备读取本地仓库";
        UpdateActions();
    }

    public async Task EnsureLoadedAsync()
    {
        if (snapshot is null && directory is not null) await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (busy || directory is null || service is null) return;
        var version = projectVersion;
        var token = projectCancellation.Token;
        busy = true;
        GitStatus.Text = "正在读取 Git 历史与工作区状态…";
        UpdateActions();
        try { await RefreshCoreAsync(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (version == projectVersion) ShowFailure("读取 Git 图谱失败", ex, dialog: false); }
        finally { if (version == projectVersion) { busy = false; UpdateActions(); } }
    }

    private async Task RefreshCoreAsync()
    {
        if (service is null || directory is null) return;
        var version = projectVersion;
        var previousHash = (CommitList.SelectedItem as GitCommitRow)?.Commit.Hash;
        var previousFilter = (BranchFilter.SelectedItem as BranchChoice)?.FullName;
        var loaded = await service.GetSnapshotAsync(directory, maxCommits, projectCancellation.Token);
        if (version != projectVersion) return;
        snapshot = loaded;
        CurrentBranchText.Text = loaded.DetachedHead ? "游离 HEAD" : loaded.CurrentBranch;
        CurrentBranchText.ToolTip = loaded.Upstream is null ? loaded.CurrentBranch : $"{loaded.CurrentBranch} → {loaded.Upstream}";
        var staged = loaded.WorkingFiles.Count(IsStaged);
        WorkingSummary.Text = $"{loaded.WorkingFiles.Count} 个文件 · {staged} 个已暂存";
        WorkingFilesList.ItemsSource = loaded.WorkingFiles.Select(file => new GitWorkingRow(file)).ToArray();
        RefsList.ItemsSource = loaded.Refs.OrderBy(r => r.Kind).ThenByDescending(r => r.IsCurrent).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new GitRefRow(r)).ToArray();
        changingFilter = true;
        BranchFilter.ItemsSource = new[] { new BranchChoice("所有分支", null, null) }
            .Concat(loaded.Refs.Where(r => r.Kind != GitRefKind.Tag)
                .Select(r => new BranchChoice(r.Name, r.FullName, r.Hash))).ToArray();
        BranchFilter.SelectedItem = BranchFilter.Items.Cast<BranchChoice>().FirstOrDefault(x => x.FullName == previousFilter) ?? BranchFilter.Items[0];
        changingFilter = false;
        ApplyFilter(previousHash);
        var sync = loaded.Upstream is null ? "未设置上游" : $"领先 {loaded.Ahead} · 落后 {loaded.Behind}";
        GitStatus.Text = $"{loaded.Commits.Count} 条提交{(loaded.HasMoreCommits ? $"（显示最近 {maxCommits} 条）" : "")} · {sync}";
    }

    private void ApplyFilter(string? selectHash = null)
    {
        if (snapshot is null) return;
        var commits = snapshot.Commits.AsEnumerable();
        if (BranchFilter.SelectedItem is BranchChoice { Hash: { } branchHash })
        {
            var byHash = snapshot.Commits.ToDictionary(c => c.Hash, StringComparer.OrdinalIgnoreCase);
            var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(); pending.Push(branchHash);
            while (pending.Count > 0)
            {
                var hash = pending.Pop();
                if (!reachable.Add(hash) || !byHash.TryGetValue(hash, out var commit)) continue;
                foreach (var parent in commit.Parents) pending.Push(parent);
            }
            commits = commits.Where(c => reachable.Contains(c.Hash));
        }
        var query = SearchBox.Text.Trim();
        if (query.Length > 0) commits = commits.Where(c =>
            c.Subject.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            c.Author.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            c.Hash.Contains(query, StringComparison.OrdinalIgnoreCase));
        var visible = commits.ToArray();
        var graph = GitGraphLayout.Build(visible.Select(c => new GitGraphCommit(c.Hash, c.Parents)));
        var labels = snapshot.Refs.GroupBy(r => r.Hash, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.Name).ToArray(), StringComparer.OrdinalIgnoreCase);
        var rows = visible.Select((commit, i) => new GitCommitRow(commit, graph[i], labels.GetValueOrDefault(commit.Hash) ?? [])).ToArray();
        CommitList.ItemsSource = rows;
        CommitList.SelectedItem = rows.FirstOrDefault(r => r.Commit.Hash == selectHash) ?? rows.FirstOrDefault();
        if (rows.Length == 0)
        {
            DetailTitle.Text = snapshot.Commits.Count == 0 ? "尚无提交" : "没有匹配的提交";
            DetailMetadata.Text = "";
            DiffBox.Text = snapshot.Commits.Count == 0 ? "先暂存文件并创建首次提交。" : "调整分支筛选或搜索词。";
            ChangedFilesList.ItemsSource = null;
        }
    }

    private static bool IsStaged(GitWorkingFile file) => file.IndexStatus is not (' ' or '?');
    private static bool IsWorkTreeChanged(GitWorkingFile file) => file.IsUntracked || file.WorkTreeStatus != ' ';

    private void UpdateActions()
    {
        var ready = !busy && directory is not null && service is not null && snapshot is not null;
        var working = WorkingFilesList.SelectedItems.Cast<GitWorkingRow>().ToArray();
        StageButton.IsEnabled = ready && working.Any(x => IsWorkTreeChanged(x.File));
        UnstageButton.IsEnabled = ready && working.Any(x => IsStaged(x.File));
        CommitButton.IsEnabled = ready && snapshot!.WorkingFiles.Any(IsStaged) && !string.IsNullOrWhiteSpace(CommitMessage.Text);
        var branch = (RefsList.SelectedItem as GitRefRow)?.Ref;
        var localOther = branch is { Kind: GitRefKind.LocalBranch, IsCurrent: false };
        CheckoutButton.IsEnabled = MergeButton.IsEnabled = DeleteBranchButton.IsEnabled = ready && localOther;
        CompareButton.IsEnabled = ready && CommitList.SelectedItem is GitCommitRow;
        LoadMoreButton.IsEnabled = ready && snapshot!.HasMoreCommits && maxCommits < 1000;
    }

    private Task MutateAsync(string label, Func<string, CancellationToken, Task> action, bool changesWorkTree = false,
        bool stageFiles = false)
    {
        Task Run(CancellationToken hostToken) => MutateCoreAsync(label, action, changesWorkTree, stageFiles, hostToken);
        return ExecuteOperationAsync is { } execute ? execute(Run) : Run(CancellationToken.None);
    }

    private async Task MutateCoreAsync(string label, Func<string, CancellationToken, Task> action,
        bool changesWorkTree, bool stageFiles, CancellationToken hostToken)
    {
        if (busy || service is null || directory is null) return;
        var version = projectVersion;
        var operationDirectory = directory;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostToken, projectCancellation.Token);
        var token = linked.Token;
        var repository = service;
        var before = snapshot;
        busy = true; IsMutating = true; MutationStateChanged?.Invoke(true);
        UpdateActions(); GitStatus.Text = label + "…";
        try
        {
            if (stageFiles && BeforeStageAsync is not null && !await BeforeStageAsync(token))
            { GitStatus.Text = "操作已取消。"; return; }
            if (changesWorkTree && BeforeWorkingTreeChangeAsync is not null && !await BeforeWorkingTreeChangeAsync(token))
            { GitStatus.Text = "操作已取消。"; return; }
            if (version != projectVersion || token.IsCancellationRequested) return;
            await action(operationDirectory, token);
            if (version != projectVersion) return;
            if (changesWorkTree && WorkingTreeChangedAsync is not null) await WorkingTreeChangedAsync(token);
            if (version != projectVersion || token.IsCancellationRequested) return;
            await RefreshCoreAsync();
            GitStatus.Text = label + "完成。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // 合并产生冲突时 Git 会返回非零退出码，但工作区已经改变；仍需重载代码与状态。
            if (version != projectVersion) return;
            if (changesWorkTree && before is not null)
            {
                try
                {
                    var after = await repository.GetSnapshotAsync(operationDirectory, maxCommits, token);
                    if (WorkingStateChanged(before, after) && WorkingTreeChangedAsync is not null)
                        await WorkingTreeChangedAsync(token);
                    await RefreshCoreAsync();
                }
                catch (Exception refreshError) { LogDiagnostic?.Invoke("Git 操作失败后刷新工作区失败：" + refreshError); }
            }
            ShowFailure(label + "失败", ex, dialog: true);
        }
        finally
        {
            IsMutating = false;
            MutationStateChanged?.Invoke(false);
            if (version == projectVersion) { busy = false; UpdateActions(); }
        }
    }

    private static bool WorkingStateChanged(GitGraphSnapshot before, GitGraphSnapshot after) =>
        !string.Equals(before.HeadCommit, after.HeadCommit, StringComparison.OrdinalIgnoreCase) ||
        !before.WorkingFiles.SequenceEqual(after.WorkingFiles);

    private void ShowFailure(string title, Exception ex, bool dialog)
    {
        LogDiagnostic?.Invoke(title + ": " + ex);
        GitStatus.Text = title + "：" + ex.Message;
        if (dialog) MessageBox.Show(Window.GetWindow(this), ex.ToString(), title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void LoadMore_Click(object sender, RoutedEventArgs e)
    {
        if (snapshot?.HasMoreCommits != true || maxCommits >= 1000) return;
        maxCommits = Math.Min(1000, maxCommits + 300);
        await RefreshAsync();
    }
    private async void Fetch_Click(object sender, RoutedEventArgs e) => await MutateAsync("获取远端引用", (dir, token) => service!.FetchAsync(dir, token: token));
    private async void Pull_Click(object sender, RoutedEventArgs e) => await MutateAsync("快进拉取", (dir, token) => service!.PullAsync(dir, token), changesWorkTree: true);
    private async void Push_Click(object sender, RoutedEventArgs e)
    {
        if (snapshot is null || service is null || directory is null) return;
        var version = projectVersion;
        var token = projectCancellation.Token;
        var currentDirectory = directory;
        var currentSnapshot = snapshot;
        if (currentSnapshot.Upstream is not null)
        { await MutateAsync("推送当前分支", (dir, token) => service.PushAsync(dir, token)); return; }
        if (currentSnapshot.DetachedHead || string.IsNullOrWhiteSpace(currentSnapshot.CurrentBranch))
        { GitStatus.Text = "游离 HEAD 无法发布分支；请先切换到本地分支。"; return; }
        try
        {
            var remotes = await service.GetRemotesAsync(currentDirectory, token);
            if (version != projectVersion) return;
            if (remotes.Count == 0)
            { GitStatus.Text = "尚未配置远端。请点击“仓库设置”添加远端。"; return; }
            var preferred = remotes.Contains("origin", StringComparer.Ordinal) ? "origin" : remotes[0];
            var remote = remotes.Count == 1 ? preferred : PromptText("首次推送", "远端名称（" + string.Join("、", remotes) + "）", preferred);
            if (string.IsNullOrWhiteSpace(remote) || !remotes.Contains(remote.Trim(), StringComparer.Ordinal)) return;
            var branch = currentSnapshot.CurrentBranch;
            if (!Confirm($"首次将本地分支 {branch} 发布到远端 {remote}，并设置上游？", "首次推送")) return;
            if (version != projectVersion) return;
            await MutateAsync("发布当前分支", (dir, token) => service.PublishBranchAsync(dir, remote.Trim(), branch, token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (version == projectVersion) ShowFailure("读取远端配置失败", ex, dialog: true); }
    }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || directory is null) { GitStatus.Text = "请先打开工程。"; return; }
        var menu = new ContextMenu { Style = (Style)FindResource("CodeContextMenu") };
        menu.Items.Add(MenuAction("设置当前仓库提交身份…", () => _ = ConfigureIdentityAsync()));
        menu.Items.Add(MenuAction("添加远端仓库…", () => _ = ConfigureRemoteAsync()));
        button.ContextMenu = menu;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }
    private async Task ConfigureIdentityAsync()
    {
        if (directory is null || service is null) return;
        var version = projectVersion;
        var token = projectCancellation.Token;
        var currentDirectory = directory;
        try
        {
            var identity = await service.GetLocalIdentityAsync(currentDirectory, token);
            if (version != projectVersion) return;
            var name = PromptText("提交身份", "姓名（仅保存在当前仓库）", identity.Name ?? "");
            if (name is null) return;
            var email = PromptText("提交身份", "邮箱（仅保存在当前仓库）", identity.Email ?? "");
            if (email is null) return;
            if (version != projectVersion) return;
            await MutateAsync("保存提交身份", (dir, token) => service.SetLocalIdentityAsync(dir, name.Trim(), email.Trim(), token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (version == projectVersion) ShowFailure("读取提交身份失败", ex, dialog: true); }
    }
    private async Task ConfigureRemoteAsync()
    {
        if (directory is null || service is null) return;
        var version = projectVersion;
        var token = projectCancellation.Token;
        var currentDirectory = directory;
        try
        {
            var remotes = await service.GetRemotesAsync(currentDirectory, token);
            if (version != projectVersion) return;
            var name = PromptText("添加远端", "远端名称（已有：" + (remotes.Count == 0 ? "无" : string.Join("、", remotes)) + "）", "origin");
            if (name is null) return;
            var url = PromptText("添加远端", "远端 URL 或本地裸仓库路径", "");
            if (url is null) return;
            if (version != projectVersion) return;
            await MutateAsync("添加远端", (dir, token) => service.AddRemoteAsync(dir, name.Trim(), url.Trim(), token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (version == projectVersion) ShowFailure("读取远端配置失败", ex, dialog: true); }
    }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) { if (!changingFilter) ApplyFilter(); }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (snapshot is not null) ApplyFilter(); }
    private void CommitMessage_Changed(object sender, TextChangedEventArgs e) => UpdateActions();
    private void WorkingFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateActions();
        if (WorkingFilesList.SelectedItems.Count == 1 && WorkingFilesList.SelectedItem is GitWorkingRow row)
            _ = ShowWorkingDiffAsync(row.File);
    }
    private void Refs_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActions();

    private async void Stage_Click(object sender, RoutedEventArgs e)
    {
        var files = WorkingFilesList.SelectedItems.Cast<GitWorkingRow>().Where(r => IsWorkTreeChanged(r.File)).Select(r => r.File.Path).ToArray();
        if (files.Length > 0) await MutateAsync("暂存文件", (dir, token) => service!.StageAsync(dir, files, token), stageFiles: true);
    }
    private async void Unstage_Click(object sender, RoutedEventArgs e)
    {
        var files = WorkingFilesList.SelectedItems.Cast<GitWorkingRow>().Where(r => IsStaged(r.File)).Select(r => r.File.Path).ToArray();
        if (files.Length > 0) await MutateAsync("取消暂存", (dir, token) => service!.UnstageAsync(dir, files, token));
    }
    private async void Commit_Click(object sender, RoutedEventArgs e)
    {
        var message = CommitMessage.Text.Trim();
        if (message.Length == 0) return;
        await MutateAsync("创建提交", (dir, token) => service!.CommitAsync(dir, message, token));
        if (GitStatus.Text == "创建提交完成。") CommitMessage.Clear();
    }

    private async void CreateBranch_Click(object sender, RoutedEventArgs e) => await CreateBranchFromAsync(null);
    private async Task CreateBranchFromAsync(string? startPoint)
    {
        if (snapshot is null) return;
        var name = PromptText("新建分支", "分支名称", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        await MutateAsync("创建分支", (dir, token) => service!.CreateBranchAsync(dir, name.Trim(), startPoint, checkout: false, token));
    }
    private async void Checkout_Click(object sender, RoutedEventArgs e)
    {
        if (RefsList.SelectedItem is not GitRefRow { Ref: { Kind: GitRefKind.LocalBranch, IsCurrent: false } selected }) return;
        await MutateAsync("切换分支", (dir, token) => service!.CheckoutBranchAsync(dir, selected.Name, token), changesWorkTree: true);
    }
    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (RefsList.SelectedItem is not GitRefRow { Ref: { Kind: GitRefKind.LocalBranch, IsCurrent: false } selected }) return;
        if (!Confirm($"将分支 {selected.Name} 合并到当前分支 {snapshot?.CurrentBranch}？\n如果发生冲突，需要在终端或编辑器中解决。", "合并分支")) return;
        await MutateAsync("合并分支", (dir, token) => service!.MergeAsync(dir, selected.Name, token), changesWorkTree: true);
    }
    private async void DeleteBranch_Click(object sender, RoutedEventArgs e)
    {
        if (RefsList.SelectedItem is not GitRefRow { Ref: { Kind: GitRefKind.LocalBranch, IsCurrent: false } selected }) return;
        if (!Confirm($"删除本地分支 {selected.Name}？\n仅允许删除已合并的分支。", "删除分支")) return;
        await MutateAsync("删除分支", (dir, token) => service!.DeleteBranchAsync(dir, selected.Name, token));
    }
    private void Refs_DoubleClick(object sender, MouseButtonEventArgs e) => Checkout_Click(sender, e);
    private bool Confirm(string message, string title) => MessageBox.Show(Window.GetWindow(this), message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private void Refs_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(RefsList, e.OriginalSource as DependencyObject) is not ListBoxItem item) return;
        RefsList.SelectedItem = item.DataContext;
        if (item.DataContext is not GitRefRow row) return;
        var menu = new ContextMenu { Style = (Style)FindResource("CodeContextMenu") };
        if (row.Ref.Kind == GitRefKind.LocalBranch && !row.Ref.IsCurrent)
        {
            menu.Items.Add(MenuAction("切换到此分支", () => Checkout_Click(this, e)));
            menu.Items.Add(MenuAction("合并到当前分支…", () => Merge_Click(this, e)));
            menu.Items.Add(MenuAction("删除本地分支…", () => DeleteBranch_Click(this, e)));
        }
        menu.Items.Add(MenuAction("从此处创建分支…", () => _ = CreateBranchFromAsync(row.Ref.Hash)));
        item.ContextMenu = menu;
    }

    private void CommitList_LeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        if (ItemsControl.ContainerFromElement(CommitList, e.OriginalSource as DependencyObject) is not ListBoxItem item || item.DataContext is not GitCommitRow target) return;
        if (CommitList.SelectedItem is not GitCommitRow start || start.Commit.Hash == target.Commit.Hash) return;
        compareBase = start;
        showingComparison = true;
        CommitList.SelectedItem = target;
        e.Handled = true;
        _ = ShowComparisonAsync(start.Commit.Hash, target.Commit.Hash);
    }
    private void CommitList_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(CommitList, e.OriginalSource as DependencyObject) is not ListBoxItem item || item.DataContext is not GitCommitRow row) return;
        CommitList.SelectedItem = row;
        var menu = new ContextMenu { Style = (Style)FindResource("CodeContextMenu") };
        menu.Items.Add(MenuAction("复制提交哈希", () => Clipboard.SetText(row.Commit.Hash)));
        menu.Items.Add(MenuAction("在此提交创建分支…", () => _ = CreateBranchFromAsync(row.Commit.Hash)));
        menu.Items.Add(MenuAction("与上一条选中提交比较", () => _ = CompareSelectedAsync()));
        item.ContextMenu = menu;
    }
    private static MenuItem MenuAction(string text, Action action)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => action();
        return item;
    }

    private void CommitList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateActions();
        if (showingComparison) { showingComparison = false; return; }
        if (CommitList.SelectedItem is GitCommitRow row) _ = ShowCommitDetailsAsync(row.Commit.Hash);
    }
    private async Task ShowCommitDetailsAsync(string hash)
    {
        if (service is null || directory is null) return;
        var version = ++detailVersion;
        selectedCommitHash = hash;
        GitStatus.Text = "正在读取提交详情…";
        try
        {
            var details = await service.GetCommitDetailsAsync(directory, hash, projectCancellation.Token);
            if (version != detailVersion || directory is null) return;
            selectedDetails = details;
            DetailTitle.Text = details.Subject;
            DetailMetadata.Text = $"{details.Author} <{details.AuthorEmail}> · {details.AuthoredAt.ToLocalTime():yyyy-MM-dd HH:mm}\n{details.Hash}";
            ChangedFilesList.ItemsSource = details.Files.Select(file => new GitChangedRow(file.Path, file.Status)).ToArray();
            if (details.Files.Count > 0) ChangedFilesList.SelectedIndex = 0;
            else DiffBox.Text = details.Message;
            GitStatus.Text = $"提交 {hash[..Math.Min(8, hash.Length)]} · {details.Files.Count} 个文件";
        }
        catch (OperationCanceledException) when (projectCancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (version == detailVersion) ShowFailure("读取提交详情失败", ex, dialog: false); }
    }
    private void ChangedFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChangedFilesList.SelectedItem is GitChangedRow file && selectedCommitHash is { } hash)
            _ = ShowCommitDiffAsync(hash, file.Path);
    }
    private async Task ShowCommitDiffAsync(string hash, string? path)
    {
        if (service is null || directory is null) return;
        var version = ++detailVersion;
        try
        {
            var diff = await service.GetDiffAsync(directory, GitDiffTarget.Commit, path, hash, projectCancellation.Token);
            if (version == detailVersion) DiffBox.Text = diff.Text + (diff.Truncated ? "\n… 差异已截断" : "");
        }
        catch (OperationCanceledException) when (projectCancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (version == detailVersion) ShowFailure("读取差异失败", ex, dialog: false); }
    }
    private async Task ShowWorkingDiffAsync(GitWorkingFile file)
    {
        if (service is null || directory is null) return;
        var project = projectVersion;
        var currentDirectory = directory;
        var token = projectCancellation.Token;
        var version = ++detailVersion;
        selectedCommitHash = null;
        selectedDetails = null;
        DetailTitle.Text = file.Path;
        DetailMetadata.Text = "工作区更改 · " + (file.IsUntracked ? "未跟踪" : $"索引 {file.IndexStatus} / 工作区 {file.WorkTreeStatus}");
        ChangedFilesList.ItemsSource = null;
        DiffBox.Text = "正在读取差异…";
        try
        {
            var parts = new List<string>();
            var truncated = false;
            if (IsStaged(file))
            {
                var staged = await service.GetDiffAsync(currentDirectory, GitDiffTarget.Staged, file.Path, token: token);
                parts.Add("=== 已暂存 ===\n" + staged.Text); truncated |= staged.Truncated;
            }
            if (project != projectVersion || token.IsCancellationRequested) return;
            if (IsWorkTreeChanged(file))
            {
                var work = await service.GetDiffAsync(currentDirectory, GitDiffTarget.WorkingTree, file.Path, token: token);
                parts.Add("=== 工作区 ===\n" + work.Text); truncated |= work.Truncated;
            }
            if (version == detailVersion && project == projectVersion) DiffBox.Text = string.Join("\n\n", parts) + (truncated ? "\n… 差异已截断" : "");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (version == detailVersion && project == projectVersion) ShowFailure("读取工作区差异失败", ex, dialog: false); }
    }
    private async void Compare_Click(object sender, RoutedEventArgs e) => await CompareSelectedAsync();
    private async Task CompareSelectedAsync()
    {
        if (CommitList.SelectedItem is not GitCommitRow selected) return;
        if (compareBase is null || compareBase.Commit.Hash == selected.Commit.Hash)
        {
            compareBase = selected;
            GitStatus.Text = $"已选 {selected.ShortHash}；按 Ctrl 点击另一条提交进行比较。";
            return;
        }
        await ShowComparisonAsync(compareBase.Commit.Hash, selected.Commit.Hash);
    }
    private async Task ShowComparisonAsync(string from, string to)
    {
        if (service is null || directory is null) return;
        var version = ++detailVersion;
        selectedCommitHash = null;
        selectedDetails = null;
        DetailTitle.Text = "提交比较";
        DetailMetadata.Text = $"{from[..Math.Min(8, from.Length)]} → {to[..Math.Min(8, to.Length)]}";
        ChangedFilesList.ItemsSource = null;
        DiffBox.Text = "正在比较提交…";
        try
        {
            var diff = await service.CompareCommitsAsync(directory, from, to, token: projectCancellation.Token);
            if (version == detailVersion)
            {
                DiffBox.Text = diff.Text + (diff.Truncated ? "\n… 差异已截断" : "");
                GitStatus.Text = "已比较两条提交。";
            }
        }
        catch (OperationCanceledException) when (projectCancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (version == detailVersion) ShowFailure("比较提交失败", ex, dialog: false); }
    }

    private string? PromptText(string title, string label, string initial)
    {
        var input = new TextBox { Text = initial, MinWidth = 310, Margin = new Thickness(0, 8, 0, 14) };
        var ok = new Button { Content = "确定", IsDefault = true, MinWidth = 76, Margin = new Thickness(7, 0, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 76, Margin = new Thickness(7, 0, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(cancel); actions.Children.Add(ok);
        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock { Text = label }); content.Children.Add(input); content.Children.Add(actions);
        var window = new Window { Owner = Window.GetWindow(this), Title = title, Content = content, Width = 380,
            MinHeight = 180, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => input.Focus();
        return window.ShowDialog() == true ? input.Text : null;
    }

    private sealed record GitCommitRow(GitCommit Commit, GitGraphRow GraphRow, IReadOnlyList<string> Refs)
    {
        public string Subject => Commit.Subject;
        public string Author => Commit.Author;
        public string DateText => Commit.AuthoredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        public string ShortHash => Commit.Hash[..Math.Min(8, Commit.Hash.Length)];
    }
    private sealed record GitWorkingRow(GitWorkingFile File)
    {
        public string Path => File.Path;
        public string Status => File.IsUntracked ? "??" : $"{File.IndexStatus}{File.WorkTreeStatus}";
    }
    private sealed record GitChangedRow(string Path, string Status);
    private sealed record GitRefRow(GitRef Ref)
    {
        public string Marker => Ref.Kind == GitRefKind.Tag ? "◆" : Ref.IsCurrent ? "●" : "○";
        public string Display => Ref.Kind switch
        {
            GitRefKind.LocalBranch => Ref.Name,
            GitRefKind.RemoteBranch => "远端 / " + Ref.Name,
            _ => "标签 / " + Ref.Name
        };
    }
    private sealed record BranchChoice(string Display, string? FullName, string? Hash);
}
