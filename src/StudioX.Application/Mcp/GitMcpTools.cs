namespace StudioX.Application.Mcp;

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using StudioX.Engine;
using StudioX.Foundation;
using static McpWorkspacePathPolicy;

/// <summary>绑定工程的 Git 查询与逐次授权写入；不访问外部参考目录。</summary>
internal sealed class GitMcpTools(McpSessionContext context) : StudioXMcpToolProvider(context)
{
    [McpServerTool(Name = "git_status")]
    [Description("读取当前工程自身仓库的分支、上游、工作区和暂存状态；不操作父目录仓库。")]
    public async Task<string> GitStatusAsync(CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await Services.GitGraph.GetSnapshotAsync(Project, 5, cancellationToken).ConfigureAwait(false);
        var safe = snapshot.WorkingFiles.Where(file => IsWorkspaceSourceFile(file.Path)).Take(101).ToArray();
        return JsonSerializer.Serialize(new
        {
            snapshot.CurrentBranch,
            snapshot.DetachedHead,
            snapshot.HeadCommit,
            snapshot.Upstream,
            snapshot.Ahead,
            snapshot.Behind,
            truncated = safe.Length > 100,
            files = safe.Take(100).Select(file => new
            {
                path = file.Path,
                file.OriginalPath,
                file.IndexStatus,
                file.WorkTreeStatus,
                file.IsUntracked
            }),
            hiddenNonSourceFiles = snapshot.WorkingFiles.Count - snapshot.WorkingFiles.Count(file => IsWorkspaceSourceFile(file.Path))
        });
    }

    [McpServerTool(Name = "git_diff")]
    [Description("读取一个安全源码文件的工作区、暂存区或指定提交差异；不返回其他工程文件或凭据文件。")]
    public async Task<string> GitDiffAsync(
        [Description("工程内一个安全源码文件的相对路径。")]
        string path,
        [Description("working、staged 或 commit。")]
        string target = "working",
        [Description("target=commit 时的完整 Git 提交哈希。")]
        string? commitHash = null,
        CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        RequireWorkspaceSourceFile(path);
        var kind = target switch
        {
            "working" => GitDiffTarget.WorkingTree,
            "staged" => GitDiffTarget.Staged,
            "commit" => GitDiffTarget.Commit,
            _ => throw new StudioXException("MCP_GIT_TARGET", "差异目标只能是 working、staged 或 commit。")
        };
        if (kind == GitDiffTarget.Commit)
        {
            RequireGitHash(commitHash);
        }
        var result = await Services.GitGraph.GetDiffAsync(Project, kind, path, commitHash, cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            path,
            target,
            truncated = result.Truncated || result.Text.Length > 16_000,
            diff = LimitOutput(result.Text)
        });
    }

    [McpServerTool(Name = "git_log")]
    [Description("读取当前工程仓库的有界提交历史，或读取一个完整提交哈希的详情。")]
    public async Task<string> GitLogAsync(
        [Description("历史数量，范围 1–30；指定 commitHash 时忽略此项。")]
        int count = 15,
        [Description("可选的完整 Git 提交哈希。")]
        string? commitHash = null,
        CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        if (commitHash is not null)
        {
            RequireGitHash(commitHash);
            var detail = await Services.GitGraph.GetCommitDetailsAsync(Project, commitHash, cancellationToken)
                .ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                detail.Hash,
                detail.Subject,
                message = LimitOutput(detail.Message, 4000),
                detail.Author,
                detail.AuthoredAt,
                files = detail.Files.Where(file => IsWorkspaceSourceFile(file.Path)).Take(60)
            });
        }
        if (count is < 1 or > 30)
        {
            throw new StudioXException("MCP_GIT_COUNT", "提交历史数量必须在 1–30 之间。");
        }
        var snapshot = await Services.GitGraph.GetSnapshotAsync(Project, count, cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            snapshot.CurrentBranch,
            snapshot.HasMoreCommits,
            commits = snapshot.Commits.Select(commit => new
            {
                commit.Hash,
                commit.Parents,
                commit.Subject,
                commit.Author,
                commit.AuthoredAt
            })
        });
    }

    [McpServerTool(Name = "git_stage")]
    [Description("按当前宿主授权策略暂存或取消暂存 1–20 个安全源码路径；不接受通配符、绝对路径或凭据文件。")]
    public async Task<string> GitStageAsync(
        [Description("stage 或 unstage。")]
        string action,
        [Description("工程内安全源码的相对路径列表，最多 20 项。")]
        string[] paths,
        CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        if (action is not ("stage" or "unstage") || paths is null || paths.Length is < 1 or > 20 ||
            paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
        {
            throw new StudioXException("MCP_GIT_STAGE", "暂存动作或路径列表无效。");
        }
        foreach (var path in paths)
        {
            RequireWorkspaceWritableSourceFile(path);
        }
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        await RequireApprovalAsync("git_stage", $"{action}：{string.Join(", ", paths)}",
            StudioXMcpPermission.GitWrite, cancellationToken).ConfigureAwait(false);
        if (action == "stage")
        {
            await Services.GitGraph.StageAsync(Project, paths, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Services.GitGraph.UnstageAsync(Project, paths, cancellationToken).ConfigureAwait(false);
        }
        return JsonSerializer.Serialize(new
        {
            action,
            paths,
            completed = true
        });
    }

    [McpServerTool(Name = "git_commit")]
    [Description("按当前宿主授权策略提交当前仓库已经暂存的内容；不会自动暂存、推送或强制修改历史。")]
    public async Task<string> GitCommitAsync(
        [Description("Git 提交说明。")]
        string message, CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(message) || message.Length > 4000 || message.Contains('\0'))
        {
            throw new StudioXException("MCP_GIT_MESSAGE", "提交说明无效或超过 4000 字符。");
        }
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        var before = await Services.GitGraph.GetSnapshotAsync(Project, 1, cancellationToken).ConfigureAwait(false);
        var staged = before.WorkingFiles.Where(file => file.IndexStatus != ' ' && file.IndexStatus != '?').ToArray();
        if (staged.Length == 0)
        {
            throw new StudioXException("MCP_GIT_EMPTY", "没有已暂存的文件。");
        }
        if (staged.Length > 20 || staged.Any(file => !IsWorkspaceWritableSourceFile(file.Path) ||
                file.OriginalPath is { } original && !IsWorkspaceWritableSourceFile(original)))
        {
            throw new StudioXException("MCP_GIT_UNSAFE", "暂存区包含安全源码范围外的文件，请先在 Git 界面检查并移除。");
        }
        var stagedDiff = await Services.GitGraph.GetDiffAsync(Project, GitDiffTarget.Staged, token: cancellationToken)
            .ConfigureAwait(false);
        if (stagedDiff.Truncated)
        {
            throw new StudioXException("MCP_GIT_DIFF", "暂存差异过大，无法安全确认提交内容。");
        }
        await RequireApprovalAsync("git_commit",
            $"提交 {staged.Length} 个已暂存源码文件；说明：{LimitOutput(message, 200)}；文件：{LimitOutput(string.Join(", ", staged.Select(file => file.Path)), 1000)}",
            StudioXMcpPermission.GitWrite, cancellationToken).ConfigureAwait(false);
        // 审批期间暂存区可能变化；重新读取，避免顺带提交用户新暂存的文件。
        var current = await Services.GitGraph.GetSnapshotAsync(Project, 1, cancellationToken).ConfigureAwait(false);
        var currentStaged = current.WorkingFiles.Where(file => file.IndexStatus != ' ' && file.IndexStatus != '?').ToArray();
        var currentDiff = await Services.GitGraph.GetDiffAsync(Project, GitDiffTarget.Staged, token: cancellationToken)
            .ConfigureAwait(false);
        if (current.HeadCommit != before.HeadCommit ||
            currentDiff.Truncated || currentDiff.Text != stagedDiff.Text ||
            !currentStaged.Select(file => file.Path + file.IndexStatus).Order(StringComparer.Ordinal)
                .SequenceEqual(staged.Select(file => file.Path + file.IndexStatus).Order(StringComparer.Ordinal)))
        {
            throw new StudioXException("MCP_GIT_CHANGED", "审批期间暂存区或 HEAD 已变化，请重新查看状态。");
        }
        await Services.GitGraph.CommitAsync(Project, message, cancellationToken).ConfigureAwait(false);
        var after = await Services.GitGraph.GetSnapshotAsync(Project, 1, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            committed = true,
            after.HeadCommit,
            after.CurrentBranch
        });
    }

    [McpServerTool(Name = "git_branch")]
    [Description("读取、创建、切换、删除已合并分支或合并本地分支。变更操作均需用户逐次批准，不提供强制删除或重置。")]
    public async Task<string> GitBranchAsync(
        [Description("list、create、switch、delete 或 merge。")]
        string action = "list",
        [Description("create/switch/delete/merge 的本地分支名称。")]
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        if (action == "list")
        {
            var snapshot = await Services.GitGraph.GetSnapshotAsync(Project, 1, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                snapshot.CurrentBranch,
                branches = snapshot.Refs.Where(reference => reference.Kind == GitRefKind.LocalBranch)
                    .Take(60).Select(reference => new { reference.Name, reference.Hash, reference.IsCurrent })
            });
        }
        if (action is not ("create" or "switch" or "delete" or "merge") ||
            string.IsNullOrWhiteSpace(name) || name.Length > 200)
        {
            throw new StudioXException("MCP_GIT_BRANCH", "分支操作或名称无效。");
        }
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        await RequireApprovalAsync("git_branch", $"{action} 本地分支 {name}",
            StudioXMcpPermission.GitWrite, cancellationToken).ConfigureAwait(false);
        switch (action)
        {
            case "create":
                await Services.GitGraph.CreateBranchAsync(Project, name, token: cancellationToken).ConfigureAwait(false);
                break;
            case "switch":
                await Services.GitGraph.CheckoutBranchAsync(Project, name, cancellationToken).ConfigureAwait(false);
                break;
            case "delete":
                await Services.GitGraph.DeleteBranchAsync(Project, name, cancellationToken).ConfigureAwait(false);
                break;
            case "merge":
                await Services.GitGraph.MergeAsync(Project, name, cancellationToken).ConfigureAwait(false);
                break;
        }
        return JsonSerializer.Serialize(new
        {
            action,
            name,
            completed = true
        });
    }

    [McpServerTool(Name = "git_remote")]
    [Description("列出远端，或逐次授权后 fetch、ff-only pull、普通 push。绝不 force push；不会克隆到其他目录。")]
    public async Task<string> GitRemoteAsync(
        [Description("list、fetch、pull 或 push。")]
        string action = "list",
        [Description("fetch 可指定的远端名称；pull/push 使用当前分支既有上游。")]
        string? remote = null,
        CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        if (action == "list")
        {
            var remotes = await Services.GitGraph.GetRemotesAsync(Project, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                remotes
            });
        }
        if (action is not ("fetch" or "pull" or "push") ||
            remote is { Length: > 0 } && (remote.Length > 128 || !char.IsAsciiLetterOrDigit(remote[0]) ||
                remote.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))) ||
            (action is "pull" or "push") && !string.IsNullOrEmpty(remote))
        {
            throw new StudioXException("MCP_GIT_REMOTE", "远端操作或名称无效。");
        }
        if (action == "pull")
        {
            await RequireSavedDocumentsAsync().ConfigureAwait(false);
        }
        await RequireApprovalAsync("git_remote", action == "fetch"
                ? $"从远端 {remote ?? "全部已配置远端"} 抓取引用。"
                : action == "pull" ? "从当前分支上游执行快进拉取。" : "将当前分支普通推送到已配置上游。",
            StudioXMcpPermission.GitRemote, cancellationToken).ConfigureAwait(false);
        switch (action)
        {
            case "fetch":
                await Services.GitGraph.FetchAsync(Project, remote, cancellationToken).ConfigureAwait(false);
                break;
            case "pull":
                await Services.GitGraph.PullAsync(Project, cancellationToken).ConfigureAwait(false);
                break;
            case "push":
                await Services.GitGraph.PushAsync(Project, cancellationToken).ConfigureAwait(false);
                break;
        }
        return JsonSerializer.Serialize(new
        {
            action,
            remote,
            completed = true
        });
    }

    private static void RequireGitHash(string? hash)
    {
        if (hash is null || hash.Length is not (40 or 64) || !hash.All(Uri.IsHexDigit))
        {
            throw new StudioXException("MCP_GIT_HASH", "需要完整的 40 或 64 位十六进制 Git 提交哈希。");
        }
    }


}
