namespace StudioX.Desktop;

using System.Text;
using System.Windows;
using System.Windows.Controls;
using StudioX.Application;
using StudioX.Foundation;

public partial class GitHubWorkspaceView
{
    private GitHubRepository? pullRequestRepository;
    private GitHubPullRequestDetails? selectedPullRequest;
    private int pullRequestVersion;
    private bool changingPullRequestFilter;

    private void InitializePullRequestControls()
    {
        changingPullRequestFilter = true;
        PrStatePicker.ItemsSource = new[]
        {
            new PullRequestStateRow("开放", GitHubPullRequestState.Open),
            new PullRequestStateRow("已关闭", GitHubPullRequestState.Closed),
            new PullRequestStateRow("全部", GitHubPullRequestState.All)
        };
        PrStatePicker.DisplayMemberPath = nameof(PullRequestStateRow.Label);
        PrStatePicker.SelectedIndex = 0;
        changingPullRequestFilter = false;
        PrMergeMethodPicker.ItemsSource = new[]
        {
            new MergeMethodRow("普通合并", GitHubMergeMethod.Merge),
            new MergeMethodRow("压缩合并", GitHubMergeMethod.Squash),
            new MergeMethodRow("变基合并", GitHubMergeMethod.Rebase)
        };
        PrMergeMethodPicker.DisplayMemberPath = nameof(MergeMethodRow.Label);
        PrMergeMethodPicker.SelectedIndex = 0;
        ClearPullRequestDetails();
    }

    private void SetPullRequestRepository(GitHubRepository? repository)
    {
        pullRequestVersion++;
        pullRequestRepository = repository;
        PrRepositoryText.Text = repository is null ? "先连接 GitHub 远端" : repository.FullName;
        PrList.ItemsSource = null;
        ClearPullRequestDetails();
        UpdatePullRequestActions();
    }

    private void UpdatePullRequestRepositoryFromRemote()
    {
        var remote = remotes.FirstOrDefault(item => item.Name == RemotePicker.Text.Trim());
        var repository = remote is { HasEmbeddedCredentials: false }
            ? GitHubPullRequestService.TryParseRepository(remote.FetchUrl)
            : null;
        if (repository != pullRequestRepository) SetPullRequestRepository(repository);
    }

    private void ClearPullRequestDetails()
    {
        selectedPullRequest = null;
        PrDetailsPanel.Visibility = Visibility.Collapsed;
        PrDetailTitle.Text = "选择一条 Pull Request";
        PrDetailMeta.Text = "";
        PrBody.Text = "";
        PrChecksText.Text = "尚未读取";
        PrReviewsText.Text = "";
        PrFilesList.ItemsSource = null;
        PrPatch.Text = "";
        PrDiscussion.Text = "";
        PrReviewBody.Text = "";
        UpdatePullRequestActions();
    }

    private void UpdatePullRequestActions()
    {
        var ready = !busy && pullRequestService is not null && pullRequestRepository is not null &&
                    AccountPicker.SelectedItem is string;
        CreatePrButton.IsEnabled = ready;
        RefreshPrButton.IsEnabled = ready;
        PrList.IsEnabled = ready;
        var open = ready && selectedPullRequest?.PullRequest is { State: "open", Merged: false };
        PrCommentButton.IsEnabled = open;
        PrApproveButton.IsEnabled = open;
        PrChangesButton.IsEnabled = open;
        PrMergeButton.IsEnabled = open && selectedPullRequest?.PullRequest.Mergeable == true;
    }

    private async Task RefreshPullRequestsAsync(CancellationToken token)
    {
        if (pullRequestService is null || pullRequestRepository is null)
        {
            PrList.ItemsSource = null;
            ClearPullRequestDetails();
            PrRepositoryText.Text = "先连接 GitHub 远端";
            return;
        }
        if (AccountPicker.SelectedItem is not string account)
        {
            PrList.ItemsSource = null;
            ClearPullRequestDetails();
            PrRepositoryText.Text = pullRequestRepository.FullName + " · 请先登录 GitHub";
            return;
        }
        var version = pullRequestVersion;
        var repo = pullRequestRepository;
        var state = (PrStatePicker.SelectedItem as PullRequestStateRow)?.State ?? GitHubPullRequestState.Open;
        var previous = (PrList.SelectedItem as PullRequestRow)?.PullRequest.Number;
        var list = await pullRequestService.ListAsync(repo, state, token, account);
        if (version != pullRequestVersion || repo != pullRequestRepository || account != AccountPicker.SelectedItem as string) return;
        PrRepositoryText.Text = repo.FullName + $" · {list.Count} 条";
        var rows = list.Select(pr => new PullRequestRow(pr)).ToArray();
        PrList.ItemsSource = rows;
        PrList.SelectedItem = rows.FirstOrDefault(row => row.PullRequest.Number == previous);
        if (PrList.SelectedItem is null) ClearPullRequestDetails();
        WorkspaceStatus.Text = $"已读取 {repo.FullName} 的 Pull Request。";
    }

    private async void RefreshPr_Click(object sender, RoutedEventArgs e) =>
        await RunActionAsync("刷新 Pull Request", RefreshPullRequestsAsync);

    private async void PrState_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (changingPullRequestFilter || pullRequestService is null || pullRequestRepository is null || busy) return;
        await RunActionAsync("筛选 Pull Request", RefreshPullRequestsAsync);
    }

    private async void Account_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (changingAccount) return;
        NotifySelectedAccountChanged();
        if (pullRequestService is null || pullRequestRepository is null || busy) return;
        pullRequestVersion++;
        PrList.ItemsSource = null;
        ClearPullRequestDetails();
        await RunActionAsync("切换 GitHub 账号", RefreshPullRequestsAsync);
    }

    private async void Pr_Changed(object sender, SelectionChangedEventArgs e)
    {
        var version = ++pullRequestVersion;
        ClearPullRequestDetails();
        if (PrList.SelectedItem is not PullRequestRow row || pullRequestService is null ||
            pullRequestRepository is null || AccountPicker.SelectedItem is not string account)
            return;
        var project = projectVersion;
        var repo = pullRequestRepository;
        PrDetailTitle.Text = "正在读取 #" + row.PullRequest.Number + "…";
        try
        {
            var details = await pullRequestService.GetDetailsAsync(repo, row.PullRequest.Number,
                account: account);
            if (version != pullRequestVersion || project != projectVersion ||
                account != AccountPicker.SelectedItem as string ||
                PrList.SelectedItem is not PullRequestRow selected || selected.PullRequest.Number != row.PullRequest.Number) return;
            DisplayPullRequest(details);
        }
        catch (Exception ex)
        {
            if (version != pullRequestVersion || project != projectVersion) return;
            PrDetailTitle.Text = "读取 Pull Request 失败";
            WorkspaceStatus.Text = ex.Message;
        }
    }

    private void DisplayPullRequest(GitHubPullRequestDetails details)
    {
        selectedPullRequest = details;
        PrDetailsPanel.Visibility = Visibility.Visible;
        var pr = details.PullRequest;
        PrDetailTitle.Text = $"#{pr.Number} {pr.Title}";
        var state = pr.Merged ? "已合并" : pr.State == "open" ? "开放" : "已关闭";
        var mergeable = pr.Mergeable switch { true => "可尝试合并", false => "存在合并冲突", _ => "GitHub 正在计算可合并状态" };
        PrDetailMeta.Text = $"{state} · {pr.Author} · {pr.HeadRepository}:{pr.Head} → {pr.BaseRepository}:{pr.Base}\n" +
                            $"更新于 {pr.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {mergeable} · {pr.MergeableState}";
        PrBody.Text = pr.Body;
        var checks = details.Checks;
        var checkLines = new List<string> { "提交状态：" + checks.CommitStatus };
        checkLines.AddRange(checks.Runs.Select(run => $"{run.Name} · {run.Status}" +
            (run.Conclusion is null ? "" : " / " + run.Conclusion)));
        if (!string.IsNullOrWhiteSpace(checks.UnavailableReason)) checkLines.Add(checks.UnavailableReason);
        PrChecksText.Text = string.Join("\n", checkLines);
        PrReviewsText.Text = details.Reviews.Count == 0 ? "尚无审阅" :
            string.Join("\n\n", details.Reviews.Select(review => $"{review.Author} · {review.State}" +
                (review.SubmittedAt is null ? "" : $" · {review.SubmittedAt.Value.ToLocalTime():yyyy-MM-dd HH:mm}") +
                (string.IsNullOrWhiteSpace(review.Body) ? "" : "\n" + review.Body)));
        PrFilesList.ItemsSource = details.Files.Select(file => new PullRequestFileRow(file)).ToArray();
        PrPatch.Text = details.Files.Count == 0 ? "没有文件改动。" : "选择文件查看补丁。";
        var timeline = details.Comments.Select(comment =>
            $"{comment.Author} · {comment.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n{comment.Body}");
        var inline = details.ReviewComments.Select(comment =>
            $"{comment.Author} · {comment.Path}:{comment.Line?.ToString() ?? "旧行"} · {comment.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n{comment.Body}");
        var discussion = timeline.Concat(inline).ToArray();
        PrDiscussion.Text = discussion.Length == 0 ? "尚无评论。" : string.Join("\n\n", discussion);
        UpdatePullRequestActions();
    }

    private void PrFile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PrFilesList.SelectedItem is not PullRequestFileRow row) return;
        PrPatch.Text = row.File.Patch ?? "GitHub 未提供此文件的文本补丁；可能是二进制文件或补丁过大。";
    }

    private async void CreatePr_Click(object sender, RoutedEventArgs e)
    {
        if (pullRequestRepository is null || pullRequestService is null ||
            AccountPicker.SelectedItem is not string account) return;
        var proposedHead = repositorySnapshot is { DetachedHead: false } snapshot ? snapshot.CurrentBranch : "";
        var repo = pullRequestRepository;
        await RunActionAsync("创建 Pull Request", async token =>
        {
            var baseBranch = await pullRequestService.GetDefaultBranchAsync(repo, token, account);
            var request = PromptCreatePullRequest(proposedHead, baseBranch);
            if (request is null)
            { WorkspaceStatus.Text = "已取消创建 Pull Request。"; return; }
            var created = await pullRequestService.CreateAsync(repo, request, token, account);
            await AfterMutationAsync($"Pull Request #{created.Number} 已创建。", async () =>
            {
                if (repo != pullRequestRepository || account != AccountPicker.SelectedItem as string) return;
                await RefreshPullRequestsAsync(token);
                PrList.SelectedItem = PrList.Items.Cast<PullRequestRow>()
                    .FirstOrDefault(row => row.PullRequest.Number == created.Number);
                WorkspaceStatus.Text = $"Pull Request #{created.Number} 已创建。";
            });
        });
    }

    private GitHubCreatePullRequest? PromptCreatePullRequest(string head, string baseBranch)
    {
        var title = new TextBox { MinWidth = 420 };
        var source = new TextBox { Text = head };
        var target = new TextBox { Text = baseBranch };
        var body = new TextBox { Height = 110, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var draft = new CheckBox { Content = "草稿 PR" };
        var save = new Button { Content = "创建", Width = 90, IsDefault = true };
        var cancel = new Button { Content = "取消", Width = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(save); actions.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(20) };
        AddField("标题", title); AddField("来源分支（fork 可用 owner:branch）", source);
        AddField("目标分支", target); AddField("说明", body);
        panel.Children.Add(draft); panel.Children.Add(actions);
        var window = new Window { Owner = Window.GetWindow(this), Title = "创建 Pull Request",
            Content = panel, Width = 500, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(title.Text) || string.IsNullOrWhiteSpace(source.Text) ||
                string.IsNullOrWhiteSpace(target.Text))
            { MessageBox.Show(window, "请填写标题、来源分支和目标分支。", "创建 Pull Request"); return; }
            window.DialogResult = true;
        };
        window.Loaded += (_, _) => title.Focus();
        return window.ShowDialog() == true
            ? new GitHubCreatePullRequest(title.Text.Trim(), body.Text.Trim(), source.Text.Trim(),
                target.Text.Trim(), draft.IsChecked == true)
            : null;

        void AddField(string label, Control input)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) });
            input.Margin = new Thickness(0, 0, 0, 11);
            panel.Children.Add(input);
        }
    }

    private async void PrComment_Click(object sender, RoutedEventArgs e) =>
        await WritePullRequestAsync("发表评论", async (repo, number, body, token, account) =>
            await pullRequestService!.CommentAsync(repo, number, body, token, account));

    private async void PrApprove_Click(object sender, RoutedEventArgs e) =>
        await WritePullRequestAsync("批准 Pull Request", async (repo, number, body, token, account) =>
            await pullRequestService!.ReviewAsync(repo, number, GitHubReviewEvent.Approve, body, token, account),
            bodyRequired: false);

    private async void PrChanges_Click(object sender, RoutedEventArgs e) =>
        await WritePullRequestAsync("请求修改", async (repo, number, body, token, account) =>
            await pullRequestService!.ReviewAsync(repo, number, GitHubReviewEvent.RequestChanges, body, token, account));

    private async Task WritePullRequestAsync(string label,
        Func<GitHubRepository, int, string, CancellationToken, string, Task> send, bool bodyRequired = true)
    {
        if (pullRequestRepository is null || selectedPullRequest is null ||
            AccountPicker.SelectedItem is not string account) return;
        var body = PrReviewBody.Text.Trim();
        if (bodyRequired && body.Length == 0)
        { WorkspaceStatus.Text = "请先填写评论或审阅说明。"; return; }
        var repo = pullRequestRepository;
        var number = selectedPullRequest.PullRequest.Number;
        if (label != "发表评论" && !Confirm($"对 #{number} 提交“{label}”审阅？", label)) return;
        await RunActionAsync(label, async token =>
        {
            await send(repo, number, body, token, account);
            PrReviewBody.Clear();
            await AfterMutationAsync($"PR #{number}：{label}已提交。", async () =>
            {
                if (repo != pullRequestRepository || account != AccountPicker.SelectedItem as string ||
                    (PrList.SelectedItem as PullRequestRow)?.PullRequest.Number != number) return;
                var details = await pullRequestService!.GetDetailsAsync(repo, number, token, account);
                DisplayPullRequest(details);
                WorkspaceStatus.Text = $"PR #{number}：{label}已提交。";
            });
        });
    }

    private async void PrMerge_Click(object sender, RoutedEventArgs e)
    {
        if (pullRequestRepository is null || pullRequestService is null ||
            selectedPullRequest?.PullRequest is not { Mergeable: true } pr ||
            AccountPicker.SelectedItem is not string account) return;
        var method = (PrMergeMethodPicker.SelectedItem as MergeMethodRow)?.Method ?? GitHubMergeMethod.Merge;
        var checks = selectedPullRequest.Checks;
        var checkSummary = checks.Runs.Count == 0 ? checks.CommitStatus :
            checks.CommitStatus + " · " + string.Join("、", checks.Runs.Select(run =>
                run.Name + ":" + (run.Conclusion ?? run.Status)));
        if (!Confirm($"将 PR #{pr.Number} 合并到 {pr.Base}？\n方式：{method}\n当前 HEAD：{pr.HeadSha}\n检查：{checkSummary}\nGitHub 将再次核对提交和分支保护规则。",
            "合并 Pull Request")) return;
        var repo = pullRequestRepository;
        await RunActionAsync("合并 Pull Request", async token =>
        {
            var result = await pullRequestService.MergeAsync(repo, pr.Number, pr.HeadSha, method, token, account);
            if (!result.Merged) throw new StudioXException("GITHUB_PR_MERGE", result.Message);
            await AfterMutationAsync($"PR #{pr.Number} 已合并。", async () =>
            {
                if (repo != pullRequestRepository || account != AccountPicker.SelectedItem as string) return;
                await RefreshPullRequestsAsync(token);
                WorkspaceStatus.Text = $"PR #{pr.Number} 已合并。";
            });
        });
    }

    private sealed record PullRequestStateRow(string Label, GitHubPullRequestState State);
    private sealed record MergeMethodRow(string Label, GitHubMergeMethod Method);
    private sealed record PullRequestRow(GitHubPullRequest PullRequest)
    {
        public string Title => $"#{PullRequest.Number} {PullRequest.Title}";
        public string Summary => $"{PullRequest.Author} · {PullRequest.Head} → {PullRequest.Base}" +
                                 (PullRequest.Draft ? " · 草稿" : "");
    }
    private sealed record PullRequestFileRow(GitHubPullRequestFile File)
    {
        public string Display => $"{File.Status}  {File.Path}  +{File.Additions} −{File.Deletions}";
    }
}
