namespace StudioX.Engine;

using System.Globalization;
using StudioX.Foundation;

public sealed partial class GitRepositoryService
{
    private readonly ProcessRunner graphRunner = new();
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(3);

    /// <summary>一次读取提交、引用和工作区；仅接受工程目录本身的仓库，避免操作父目录仓库。</summary>
    public async Task<GitGraphSnapshot> GetSnapshotAsync(string directory, int maxCommits = 200, CancellationToken token = default)
    {
        if (maxCommits is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maxCommits));
        var root = await RequireRepositoryAsync(directory, token);
        var branchTask = RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], ReadTimeout, token);
        var headTask = RunAsync(root, ["rev-parse", "--verify", "HEAD"], ReadTimeout, token);
        var logTask = RunAsync(root, ["log", "--all", "--topo-order", "--date-order", $"--max-count={maxCommits + 1}",
            "--format=%H%x00%P%x00%aI%x00%an%x00%s%x00"], ReadTimeout, token);
        var refsTask = RunAsync(root, ["for-each-ref", "--format=%(refname)%00%(objectname)%00%(*objectname)%00",
            "refs/heads", "refs/remotes", "refs/tags"], ReadTimeout, token);
        var statusTask = RunAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], ReadTimeout, token);
        await Task.WhenAll(branchTask, headTask, logTask, refsTask, statusTask);
        var branchResult = await branchTask;
        var headResult = await headTask;
        var head = headResult.Success ? headResult.StandardOutput.Trim() : null;
        var detached = !branchResult.Success && head is not null;
        var branch = branchResult.Success ? branchResult.StandardOutput.Trim() : detached ? "(detached HEAD)" : "(unborn HEAD)";
        var commits = ParseCommits(Checked(await logTask, "读取提交历史"));
        var hasMore = commits.Count > maxCommits;
        if (hasMore) commits.RemoveAt(commits.Count - 1);
        var refs = ParseRefs(Checked(await refsTask, "读取引用"), branch);
        var files = ParseStatus(Checked(await statusTask, "读取工作区"));
        string? upstream = null;
        var ahead = 0;
        var behind = 0;
        if (head is not null && !detached)
        {
            var upstreamResult = await RunAsync(root, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"], ReadTimeout, token);
            if (upstreamResult.Success)
            {
                upstream = upstreamResult.StandardOutput.Trim();
                var count = Checked(await RunAsync(root, ["rev-list", "--left-right", "--count", "HEAD...@{upstream}"], ReadTimeout, token), "读取领先/落后数量");
                var parts = count.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out ahead)
                    && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out behind)) { }
                else throw new StudioXException("GIT_PARSE", "Git 返回了无法识别的领先/落后数量。\n" + count);
            }
        }
        return new(root, branch, detached, head, commits, refs, files, upstream, ahead, behind, hasMore);
    }

    public async Task<GitCommitDetails> GetCommitDetailsAsync(string directory, string commitHash, CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        RequireHash(commitHash);
        var metadata = Checked(await RunAsync(root, ["show", "-s", "--format=%H%x00%s%x00%B%x00%an%x00%ae%x00%aI%x00%cn%x00%ce%x00%cI%x00", commitHash], ReadTimeout, token), "读取提交详情");
        var fields = metadata.Split('\0');
        if (fields.Length < 10 || !DateTimeOffset.TryParse(fields[5], CultureInfo.InvariantCulture, DateTimeStyles.None, out var authored)
            || !DateTimeOffset.TryParse(fields[8], CultureInfo.InvariantCulture, DateTimeStyles.None, out var committed))
            throw new StudioXException("GIT_PARSE", "Git 返回了无法识别的提交详情。\n" + metadata);
        var parents = await ParentsAsync(root, commitHash, token);
        var fileArgs = parents.Count == 0
            ? new[] { "diff-tree", "--root", "--no-commit-id", "--name-status", "-r", "-z", commitHash }
            : new[] { "diff", "--name-status", "-z", "--find-renames", parents[0], commitHash };
        var files = ParseChangedFiles(Checked(await RunAsync(root, fileArgs, ReadTimeout, token), "读取提交文件"));
        return new(fields[0].TrimStart('\r', '\n'), fields[1], fields[2].TrimEnd('\r', '\n'), fields[3], fields[4], authored,
            fields[6], fields[7], committed, files);
    }

    public async Task<GitDiffResult> GetDiffAsync(string directory, GitDiffTarget target, string? path = null,
        string? commitHash = null, CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        string[] pathArgs = path is null ? [] : ["--", LiteralPath(root, path)];
        string[] args;
        switch (target)
        {
            case GitDiffTarget.WorkingTree:
                if (path is not null)
                {
                    var tracked = await RunAsync(root, ["ls-files", "--error-unmatch", "--", LiteralPath(root, path)], ReadTimeout, token);
                    var fullPath = Path.GetFullPath(Path.Combine(root, path));
                    if (!tracked.Success && File.Exists(fullPath))
                    {
                        var untracked = await RunAsync(root, ["diff", "--no-index", "--no-ext-diff", "--no-textconv", "--no-color", "--", "NUL", fullPath], ReadTimeout, token);
                        if (untracked.ExitCode is not (0 or 1) || untracked.TimedOut) throw CommandError("读取未跟踪文件差异", untracked);
                        return new(untracked.StandardOutput, untracked.OutputTruncated);
                    }
                }
                args = ["diff", "--no-ext-diff", "--no-textconv", "--no-color", "--find-renames", .. pathArgs];
                break;
            case GitDiffTarget.Staged:
                args = ["diff", "--cached", "--no-ext-diff", "--no-textconv", "--no-color", "--find-renames", .. pathArgs];
                break;
            case GitDiffTarget.Commit:
                RequireHash(commitHash);
                var parents = await ParentsAsync(root, commitHash!, token);
                args = parents.Count == 0
                    ? ["show", "--format=", "--no-ext-diff", "--no-textconv", "--no-color", commitHash!, .. pathArgs]
                    : ["diff", "--no-ext-diff", "--no-textconv", "--no-color", "--find-renames", parents[0], commitHash!, .. pathArgs];
                break;
            default: throw new ArgumentOutOfRangeException(nameof(target));
        }
        var result = await RunAsync(root, args, ReadTimeout, token);
        if (!result.Success) throw CommandError("读取差异", result);
        return new(result.StandardOutput, result.OutputTruncated);
    }

    public async Task<GitDiffResult> CompareCommitsAsync(string directory, string fromHash, string toHash,
        string? path = null, CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        RequireHash(fromHash); RequireHash(toHash);
        var pathArgs = path is null ? Array.Empty<string>() : new[] { "--", LiteralPath(root, path) };
        var result = await RunAsync(root, ["diff", "--no-ext-diff", "--no-textconv", "--no-color", "--find-renames",
            fromHash, toHash, .. pathArgs], ReadTimeout, token);
        if (!result.Success) throw CommandError("比较提交", result);
        return new(result.StandardOutput, result.OutputTruncated);
    }

    public Task StageAsync(string directory, IReadOnlyList<string> paths, CancellationToken token = default) =>
        ChangePathsAsync(directory, paths, "暂存文件", root => ["add", "--", .. paths.Select(p => LiteralPath(root, p))], token);

    public async Task UnstageAsync(string directory, IReadOnlyList<string> paths, CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        var pathArgs = ValidatePaths(root, paths);
        var head = await RunAsync(root, ["rev-parse", "--verify", "HEAD"], ReadTimeout, token);
        string[] args = head.Success
            ? ["restore", "--staged", "--", .. pathArgs]
            : ["rm", "--cached", "-r", "--", .. pathArgs];
        Checked(await RunAsync(root, args, WriteTimeout, token), "取消暂存");
    }

    public async Task CommitAsync(string directory, string message, CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        if (string.IsNullOrWhiteSpace(message) || message.Length > 100_000 || message.Contains('\0'))
            throw new StudioXException("GIT_MESSAGE", "提交说明不能为空或无效。");
        Checked(await RunAsync(root, ["commit", "-m", message], WriteTimeout, token), "提交");
    }

    public async Task CreateBranchAsync(string directory, string name, string? startPoint = null,
        bool checkout = false, CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        await RequireBranchNameAsync(root, name, token);
        if (startPoint is not null) RequireHash(startPoint);
        string[] args = checkout
            ? startPoint is null ? ["switch", "-c", name] : ["switch", "-c", name, startPoint]
            : startPoint is null ? ["branch", name] : ["branch", name, startPoint];
        Checked(await RunAsync(root, args, WriteTimeout, token), checkout ? "创建并切换分支" : "创建分支");
    }

    public async Task CheckoutBranchAsync(string directory, string name, CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        await RequireLocalBranchAsync(root, name, token);
        Checked(await RunAsync(root, ["switch", name], WriteTimeout, token), "切换分支");
    }

    public async Task DeleteBranchAsync(string directory, string name, CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        await RequireLocalBranchAsync(root, name, token);
        // -d 仅删除已合并的分支；GUI 不提供强制删分支。
        Checked(await RunAsync(root, ["branch", "-d", name], WriteTimeout, token), "删除分支");
    }

    public async Task MergeAsync(string directory, string branchName, CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        await RequireLocalBranchAsync(root, branchName, token);
        Checked(await RunAsync(root, ["merge", "--no-edit", branchName], WriteTimeout, token), "合并分支");
    }

    public Task FetchAsync(string directory, string? remote = null, CancellationToken token = default) =>
        FetchAsync(directory, remote, selectedAccount: null, token);

    public async Task FetchAsync(string directory, string? remote, string? selectedAccount,
        CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        var remotes = remote is null ? await RemoteNamesAsync(root, token) : new[] { remote };
        if (remotes.Count == 0)
            throw new StudioXException("GIT_REMOTE", "当前仓库还没有远端；请先连接 GitHub 仓库。");
        foreach (var selected in remotes)
        {
            await RequireRemoteAsync(root, selected, token);
            var url = await RemoteUrlAsync(root, selected, push: false, token);
            NetworkChecked(await RunNetworkAsync(root, ["fetch", "--no-recurse-submodules", selected], url,
                selectedAccount, token),
                "抓取远端更新");
        }
    }

    public Task PullAsync(string directory, CancellationToken token = default) =>
        PullAsync(directory, selectedAccount: null, token);

    public async Task PullAsync(string directory, string? selectedAccount, CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        var branch = Checked(await RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], ReadTimeout, token),
            "读取当前分支").Trim();
        var upstream = Checked(await RunAsync(root, ["for-each-ref",
            "--format=%(upstream:remotename)%00%(upstream:remoteref)%00%(upstream)", "refs/heads/" + branch],
            ReadTimeout, token), "读取上游分支").TrimEnd('\r', '\n').Split('\0');
        if (upstream.Length < 3 || !ValidRemoteName(upstream[0])
            || !upstream[1].StartsWith("refs/heads/", StringComparison.Ordinal)
            || !upstream[2].StartsWith("refs/remotes/", StringComparison.Ordinal))
            throw new StudioXException("GIT_UPSTREAM", "当前分支尚未设置上游分支；请先选择远端分支并设置上游。");
        await RequireRemoteAsync(root, upstream[0], token);
        var url = await RemoteUrlAsync(root, upstream[0], push: false, token);
        NetworkChecked(await RunNetworkAsync(root, ["fetch", "--no-recurse-submodules", upstream[0]], url,
            selectedAccount, token),
            "抓取上游更新");
        await RequireRefAsync(root, upstream[2], "上游跟踪分支不存在；请检查远端分支是否已删除。", token);
        // 先检测分歧，再用 ff-only 更新；工作树若与远端冲突，Git 会拒绝并保留本地文件。
        var ancestor = await RunAsync(root, ["merge-base", "--is-ancestor", "HEAD", upstream[2]], ReadTimeout, token);
        if (!ancestor.Success)
        {
            if (ancestor.ExitCode != 1 || ancestor.TimedOut) throw CommandError("检查分支祖先关系", ancestor);
            var remoteAncestor = await RunAsync(root,
                ["merge-base", "--is-ancestor", upstream[2], "HEAD"], ReadTimeout, token);
            if (remoteAncestor.Success) return; // 本地已领先于远端，无需改动工作区。
            if (remoteAncestor.ExitCode != 1 || remoteAncestor.TimedOut)
                throw CommandError("检查分支祖先关系", remoteAncestor);
            throw new StudioXException("GIT_DIVERGED", "本地与远端分支已经分叉；拉取未更改提交或工作区。请查看分支图后手动合并。");
        }
        var result = await RunAsync(root, ["merge", "--ff-only", "--no-edit", upstream[2]], WriteTimeout, token);
        if (!result.Success) throw WorkingTreeChangeError("快进拉取", result);
    }

    public Task PushAsync(string directory, CancellationToken token = default) =>
        PushAsync(directory, selectedAccount: null, token);

    public async Task PushAsync(string directory, string? selectedAccount, CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        var branch = Checked(await RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], ReadTimeout, token), "读取当前分支").Trim();
        var upstream = Checked(await RunAsync(root, ["for-each-ref", "--format=%(upstream:remotename)%00%(upstream:remoteref)",
            "refs/heads/" + branch], ReadTimeout, token), "读取上游分支").TrimEnd('\r', '\n').Split('\0');
        if (upstream.Length < 2 || string.IsNullOrWhiteSpace(upstream[0]) || !upstream[1].StartsWith("refs/heads/", StringComparison.Ordinal))
            throw new StudioXException("GIT_UPSTREAM", "当前分支尚未配置可推送的上游分支；请先选择远端并发布分支。");
        await RequireRemoteAsync(root, upstream[0], token);
        await RequireNonMirrorRemoteAsync(root, upstream[0], token);
        var refspec = $"refs/heads/{branch}:{upstream[1]}";
        var url = await RemoteUrlAsync(root, upstream[0], push: true, token);
        NetworkChecked(await RunNetworkAsync(root, ["push", "--no-follow-tags", upstream[0], refspec], url,
            selectedAccount, token),
            "推送到远端");
    }

    public async Task<GitIdentity> GetLocalIdentityAsync(string directory, CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        var name = await RunAsync(root, ["config", "--local", "--get", "user.name"], ReadTimeout, token);
        var email = await RunAsync(root, ["config", "--local", "--get", "user.email"], ReadTimeout, token);
        if (name.ExitCode is not (0 or 1) || name.TimedOut) throw CommandError("读取提交姓名", name);
        if (email.ExitCode is not (0 or 1) || email.TimedOut) throw CommandError("读取提交邮箱", email);
        return new(name.Success ? name.StandardOutput.TrimEnd('\r', '\n') : null,
            email.Success ? email.StandardOutput.TrimEnd('\r', '\n') : null);
    }

    public async Task SetLocalIdentityAsync(string directory, string name, string email, CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email) || name.Length > 256 || email.Length > 320
            || name.IndexOfAny(['\0', '\r', '\n']) >= 0 || email.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new StudioXException("GIT_IDENTITY", "提交姓名或邮箱不能为空，且不能包含换行。");
        Checked(await RunAsync(root, ["config", "--local", "user.name", name], WriteTimeout, token), "设置工程提交姓名");
        Checked(await RunAsync(root, ["config", "--local", "user.email", email], WriteTimeout, token), "设置工程提交邮箱");
    }

    public async Task<IReadOnlyList<string>> GetRemotesAsync(string directory, CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        return await RemoteNamesAsync(root, token);
    }

    public async Task AddRemoteAsync(string directory, string name, string url, CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        // 远端名称限定为常用字符，避免选项形式和 refspec 特殊字符进入后续 push 命令。
        if (!ValidRemoteName(name))
            throw new StudioXException("GIT_REMOTE", "远端名称只能由英文字母、数字、点、下划线和短横线组成，且不能以标点开头。");
        ValidateRemoteUrl(url);
        var result = await RunAsync(root, ["remote", "add", name, url], WriteTimeout, token);
        if (!result.Success) throw new StudioXException("GIT_REMOTE", "添加远端失败；请检查名称是否已存在。原始输出已隐藏以保护凭据。");
    }

    public Task PublishBranchAsync(string directory, string remote, string branch, CancellationToken token = default) =>
        PublishBranchAsync(directory, remote, branch, selectedAccount: null, token);

    public async Task PublishBranchAsync(string directory, string remote, string branch, string? selectedAccount,
        CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        await RequireRemoteAsync(root, remote, token);
        await RequireNonMirrorRemoteAsync(root, remote, token);
        await RequireLocalBranchAsync(root, branch, token);
        // 只发布明确选定的本地分支；不强推，也不发布其它引用。
        var refspec = $"refs/heads/{branch}:refs/heads/{branch}";
        var url = await RemoteUrlAsync(root, remote, push: true, token);
        NetworkChecked(await RunNetworkAsync(root, ["push", "--no-follow-tags", "--set-upstream", remote, refspec], url,
            selectedAccount, token),
            "首次发布分支");
    }

    private async Task ChangePathsAsync(string directory, IReadOnlyList<string> paths, string operation,
        Func<string, string[]> command, CancellationToken token)
    {
        var root = await RequireRepositoryAsync(directory, token);
        ValidatePaths(root, paths);
        Checked(await RunAsync(root, command(root), WriteTimeout, token), operation);
    }

    private async Task<string> RequireRepositoryAsync(string directory, CancellationToken token)
    {
        RequireAvailable();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new StudioXException("GIT_DIRECTORY", "工程目录不存在。\n" + directory);
        var requested = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var result = await RunAsync(requested, ["rev-parse", "--show-toplevel"], ReadTimeout, token);
        if (!result.Success) throw new StudioXException("GIT_NOT_REPOSITORY", "当前工程没有 Git 仓库。\n" + result.StandardError.Trim());
        var actual = Path.GetFullPath(result.StandardOutput.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!actual.Equals(requested, StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("GIT_NOT_PROJECT_REPOSITORY", "当前目录属于上级 Git 仓库；图形模式仅操作工程根目录自己的仓库。\n" + actual);
        return requested;
    }

    private async Task<ProcessResult> RunAsync(string root, IReadOnlyList<string> args, TimeSpan timeout,
        CancellationToken token, string? githubCredentialUrl = null, string? selectedAccount = null)
    {
        var credentialArgs = githubCredentialUrl is null ? [] : GitHubCredentialCommandArguments(githubCredentialUrl, selectedAccount);
        string[] allArgs = ["--no-pager", "-c", "core.quotepath=false", "-c", "color.ui=false", .. credentialArgs, .. args];
        var environment = Environment();
        // 禁止在无控制台进程中等待命令行输入；保留系统凭据管理器的图形登录能力。
        environment["GIT_TERMINAL_PROMPT"] = "0";
        if (githubCredentialUrl is not null)
        {
            environment["GCM_CREDENTIAL_STORE"] = "wincredman";
            environment["GCM_TRACE"] = "0";
            environment["GCM_TRACE_SECRETS"] = "0";
        }
        var remove = githubCredentialUrl is not null
            ? [.. AmbientVariables, .. System.Environment.GetEnvironmentVariables().Keys.Cast<string>()
                .Where(k => k.StartsWith("GCM_", StringComparison.OrdinalIgnoreCase))]
            : AmbientVariables;
        return await graphRunner.RunAsync(new(Executable, allArgs, root, timeout, environment,
            RemoveEnvironment: remove), token);
    }

    private static string Checked(ProcessResult result, string operation)
    {
        if (!result.Success || result.OutputTruncated) throw CommandError(operation, result);
        return result.StandardOutput;
    }

    private static StudioXException CommandError(string operation, ProcessResult result) =>
        new(result.TimedOut ? "GIT_TIMEOUT" : result.OutputTruncated ? "GIT_OUTPUT_LIMIT" : "GIT_COMMAND",
            $"Git {operation}失败（退出码 {result.ExitCode}）：\n{result.StandardError}{result.StandardOutput}");

    private static List<GitCommit> ParseCommits(string output)
    {
        var parts = output.Split('\0');
        var commits = new List<GitCommit>();
        for (var i = 0; i + 4 < parts.Length; i += 5)
        {
            var hash = parts[i].TrimStart('\r', '\n');
            if (hash.Length == 0) break;
            if (!DateTimeOffset.TryParse(parts[i + 2], CultureInfo.InvariantCulture, DateTimeStyles.None, out var authored))
                throw new StudioXException("GIT_PARSE", "Git 返回了无法识别的提交时间。\n" + parts[i + 2]);
            commits.Add(new(hash, parts[i + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries), parts[i + 4], parts[i + 3], authored));
        }
        return commits;
    }

    private static List<GitRef> ParseRefs(string output, string branch)
    {
        var parts = output.Split('\0');
        var refs = new List<GitRef>();
        for (var i = 0; i + 2 < parts.Length; i += 3)
        {
            var fullName = parts[i].TrimStart('\r', '\n');
            if (fullName.Length == 0) break;
            GitRefKind kind;
            string name;
            if (fullName.StartsWith("refs/heads/", StringComparison.Ordinal))
            { kind = GitRefKind.LocalBranch; name = fullName[11..]; }
            else if (fullName.StartsWith("refs/remotes/", StringComparison.Ordinal))
            { kind = GitRefKind.RemoteBranch; name = fullName[13..]; if (name.EndsWith("/HEAD", StringComparison.Ordinal)) continue; }
            else if (fullName.StartsWith("refs/tags/", StringComparison.Ordinal))
            { kind = GitRefKind.Tag; name = fullName[10..]; }
            else continue;
            var peeled = parts[i + 2].TrimEnd('\r', '\n');
            refs.Add(new(name, fullName, peeled.Length > 0 ? peeled : parts[i + 1], kind,
                kind == GitRefKind.LocalBranch && name == branch));
        }
        return refs;
    }

    private static List<GitWorkingFile> ParseStatus(string output)
    {
        var files = new List<GitWorkingFile>();
        var pos = 0;
        while (pos < output.Length)
        {
            if (pos + 3 > output.Length || output[pos + 2] != ' ')
                throw new StudioXException("GIT_PARSE", "Git 返回了无法识别的工作区状态。");
            var x = output[pos]; var y = output[pos + 1];
            pos += 3;
            var end = output.IndexOf('\0', pos);
            if (end < 0) throw new StudioXException("GIT_PARSE", "Git 工作区状态缺少路径终止符。");
            var path = output[pos..end]; pos = end + 1;
            string? original = null;
            if (x is 'R' or 'C' || y is 'R' or 'C')
            {
                end = output.IndexOf('\0', pos);
                if (end < 0) throw new StudioXException("GIT_PARSE", "Git 重命名状态缺少原路径。");
                original = output[pos..end]; pos = end + 1;
            }
            files.Add(new(path, original, x, y, x == '?' && y == '?'));
        }
        return files;
    }

    private static List<GitChangedFile> ParseChangedFiles(string output)
    {
        var files = new List<GitChangedFile>();
        var parts = output.Split('\0');
        for (var i = 0; i < parts.Length && parts[i].Length > 0;)
        {
            var status = parts[i++].TrimStart('\r', '\n');
            if (status.Length == 0) break;
            if (i >= parts.Length) throw new StudioXException("GIT_PARSE", "Git 文件差异缺少路径。");
            var path = parts[i++];
            string? original = null;
            if (status[0] is 'R' or 'C')
            {
                original = path;
                if (i >= parts.Length) throw new StudioXException("GIT_PARSE", "Git 重命名差异缺少新路径。");
                path = parts[i++];
            }
            files.Add(new(path, original, status));
        }
        return files;
    }

    private async Task<IReadOnlyList<string>> ParentsAsync(string root, string hash, CancellationToken token)
    {
        var line = Checked(await RunAsync(root, ["rev-list", "--parents", "-n", "1", hash], ReadTimeout, token), "读取提交父节点").Trim();
        var hashes = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (hashes.Length == 0 || !hashes[0].Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("GIT_PARSE", "Git 返回了无法识别的提交父节点。\n" + line);
        return hashes.Skip(1).ToArray();
    }

    private static void RequireHash(string? hash)
    {
        if (hash is null || hash.Length is not (40 or 64) || !hash.All(Uri.IsHexDigit))
            throw new StudioXException("GIT_HASH", "提交 ID 必须是完整的 40 或 64 位十六进制哈希。");
    }

    private static string[] ValidatePaths(string root, IReadOnlyList<string> paths)
    {
        if (paths is null || paths.Count == 0) throw new StudioXException("GIT_PATH", "请选择至少一个文件。");
        if (paths.Count > 1000) throw new StudioXException("GIT_PATH", "一次最多操作 1000 个文件。");
        return paths.Select(p => LiteralPath(root, p)).ToArray();
    }

    private static string LiteralPath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || Path.IsPathRooted(path))
            throw new StudioXException("GIT_PATH", "文件路径必须位于当前工程内。");
        var full = Path.GetFullPath(Path.Combine(root, path));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("GIT_PATH", "文件路径超出了当前工程。");
        var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        return ":(literal)" + relative;
    }

    private async Task RequireBranchNameAsync(string root, string name, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(name) || name[0] == '-' || name.Contains('\0'))
            throw new StudioXException("GIT_BRANCH", "分支名称无效。");
        var result = await RunAsync(root, ["check-ref-format", "--branch", name], ReadTimeout, token);
        if (!result.Success) throw new StudioXException("GIT_BRANCH", "分支名称无效。\n" + result.StandardError.Trim());
    }

    private async Task RequireLocalBranchAsync(string root, string name, CancellationToken token)
    {
        await RequireBranchNameAsync(root, name, token);
        var result = await RunAsync(root, ["show-ref", "--verify", "--quiet", "refs/heads/" + name], ReadTimeout, token);
        if (!result.Success) throw new StudioXException("GIT_BRANCH", "本地分支不存在：" + name);
    }

    private async Task RequireRemoteAsync(string root, string remote, CancellationToken token)
    {
        if (!ValidRemoteName(remote)) throw new StudioXException("GIT_REMOTE", "远端名称无效。");
        var result = Checked(await RunAsync(root, ["remote"], ReadTimeout, token), "读取远端列表");
        if (!result.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Contains(remote, StringComparer.Ordinal))
            throw new StudioXException("GIT_REMOTE", "远端不存在：" + remote);
    }

    private async Task RequireNonMirrorRemoteAsync(string root, string remote, CancellationToken token)
    {
        var result = await RunAsync(root, ["config", "--bool", "--get", $"remote.{remote}.mirror"], ReadTimeout, token);
        if (result.ExitCode is not (0 or 1) || result.TimedOut) throw CommandError("读取远端推送配置", result);
        if (result.Success && result.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("GIT_MIRROR", "该远端启用了镜像推送；图形模式只允许推送明确选定的单个分支。");
    }

    private static bool ValidRemoteName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 128 && char.IsAsciiLetterOrDigit(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
