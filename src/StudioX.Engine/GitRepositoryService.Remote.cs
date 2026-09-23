namespace StudioX.Engine;

using System.Text.RegularExpressions;
using StudioX.Foundation;

public sealed partial class GitRepositoryService
{
    /// <summary>将远端地址以不含内嵌凭据的形式提供给界面。</summary>
    public async Task<IReadOnlyList<GitRemoteInfo>> GetRemoteDetailsAsync(string directory, CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        var names = await RemoteNamesAsync(root, token);
        var details = new List<GitRemoteInfo>(names.Count);
        foreach (var name in names)
        {
            var fetch = await RemoteUrlAsync(root, name, push: false, token);
            var push = await RemoteUrlAsync(root, name, push: true, token);
            details.Add(new(name, DisplayRemoteUrl(fetch), DisplayRemoteUrl(push),
                IsGitHubRemoteUrl(fetch), ContainsEmbeddedCredentials(fetch) || ContainsEmbeddedCredentials(push)));
        }
        return details;
    }

    /// <summary>以独立临时目录克隆，再移动到目标位置；已存在的目标目录绝不写入。</summary>
    public Task<string> CloneAsync(string url, string destinationDirectory, CancellationToken token = default) =>
        CloneAsync(url, destinationDirectory, selectedAccount: null, token);

    public async Task<string> CloneAsync(string url, string destinationDirectory, string? selectedAccount,
        CancellationToken token = default)
    {
        RequireAvailable();
        if (ContainsEmbeddedCredentials(url))
            throw new StudioXException("GIT_REMOTE_CREDENTIALS", "克隆地址内含账号或凭据；请使用不带凭据的 HTTPS 地址并通过 GitHub 登录授权。");
        ValidateRemoteUrl(url);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new StudioXException("GIT_CLONE_PATH", "请选择克隆目标目录。");
        string destination;
        try { destination = Path.GetFullPath(destinationDirectory); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new StudioXException("GIT_CLONE_PATH", "克隆目标目录无效。", ex); }
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new StudioXException("GIT_CLONE_EXISTS", "克隆目标已存在；请选择一个新的空目录名称，现有文件未修改。");
        var parent = Path.GetDirectoryName(destination);
        if (parent is null || !Directory.Exists(parent))
            throw new StudioXException("GIT_CLONE_PATH", "克隆目标的上级目录不存在。");
        var stage = Path.GetFullPath(Path.Combine(parent, ".studiox-clone-" + Guid.NewGuid().ToString("N")));
        var parentPrefix = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!stage.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("GIT_CLONE_PATH", "克隆临时目录超出了目标位置。");
        Directory.CreateDirectory(stage);
        try
        {
            var result = await RunAsync(parent, ["clone", "--origin=origin", "--", url, stage], TimeSpan.FromMinutes(10),
                token, githubCredentialUrl: IsGitHubHttpsUrl(url) ? url : null, selectedAccount: selectedAccount);
            NetworkChecked(result, "克隆仓库");
            if (!Directory.Exists(Path.Combine(stage, ".git")) && !File.Exists(Path.Combine(stage, ".git")))
                throw new StudioXException("GIT_CLONE", "Git 克隆命令成功，但目标没有工作区仓库。");
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new StudioXException("GIT_CLONE_EXISTS", "克隆期间目标目录被其它程序创建；现有文件未修改。");
            Directory.Move(stage, destination);
            return destination;
        }
        finally
        {
            // stage 名称由本服务生成，并且已验证是 parent 的直接子目录。
            if (Directory.Exists(stage))
            {
                try { Directory.Delete(stage, recursive: true); }
                catch (IOException) { /* 不掩盖克隆的原始错误；下次可检查残留临时目录。 */ }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public async Task SetRemoteUrlAsync(string directory, string name, string url, CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        await RequireRemoteAsync(root, name, token);
        ValidateRemoteUrl(url);
        var result = await RunAsync(root, ["remote", "set-url", name, url], WriteTimeout, token);
        if (!result.Success) throw new StudioXException("GIT_REMOTE", "设置远端地址失败；请检查远端配置。原始输出已隐藏以保护凭据。");
    }

    /// <summary>显式修复独立 pushurl；一次仅处理一个地址，避免意外改动多个仓库。</summary>
    public async Task SetRemotePushUrlAsync(string directory, string remote, string url, CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        await RequireRemoteAsync(root, remote, token);
        ValidateRemoteUrl(url);
        _ = await RemoteUrlAsync(root, remote, push: true, token); // 多推送地址不在图形模式中自动猜选。
        var result = await RunAsync(root, ["remote", "set-url", "--push", remote, url], WriteTimeout, token);
        if (!result.Success)
            throw new StudioXException("GIT_REMOTE", "设置推送地址失败；请检查远端配置。原始输出已隐藏以保护凭据。");
    }

    public async Task<IReadOnlyList<GitRemoteBranch>> GetRemoteBranchesAsync(string directory, string? remote = null,
        CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        if (remote is not null) await RequireRemoteAsync(root, remote, token);
        var format = "%(refname)%00%(objectname)%00";
        var prefix = remote is null ? "refs/remotes" : "refs/remotes/" + remote;
        var output = Checked(await RunAsync(root, ["for-each-ref", "--format=" + format, prefix], ReadTimeout, token),
            "读取远端分支");
        var currentUpstream = await RunAsync(root,
            ["rev-parse", "--symbolic-full-name", "@{upstream}"], ReadTimeout, token);
        var upstream = currentUpstream.Success ? currentUpstream.StandardOutput.Trim() : null;
        var branches = new List<GitRemoteBranch>();
        var parts = output.Split('\0');
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            var full = parts[i].TrimStart('\r', '\n');
            if (full.Length == 0) break;
            if (!full.StartsWith("refs/remotes/", StringComparison.Ordinal) || full.EndsWith("/HEAD", StringComparison.Ordinal))
                continue;
            var shortName = full[13..];
            var slash = shortName.IndexOf('/');
            if (slash <= 0 || slash == shortName.Length - 1) continue;
            var remoteName = shortName[..slash];
            if (remote is not null && !remoteName.Equals(remote, StringComparison.Ordinal)) continue;
            branches.Add(new(remoteName, shortName[(slash + 1)..], full, parts[i + 1].TrimEnd('\r', '\n'),
                full.Equals(upstream, StringComparison.Ordinal)));
        }
        return branches;
    }

    public async Task CheckoutRemoteBranchAsync(string directory, string remote, string branch,
        string? localBranch = null, CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        await RequireRemoteAsync(root, remote, token);
        await RequireBranchNameAsync(root, branch, token);
        localBranch ??= branch;
        await RequireBranchNameAsync(root, localBranch, token);
        var remoteRef = $"refs/remotes/{remote}/{branch}";
        await RequireRefAsync(root, remoteRef, "远端跟踪分支尚不存在；请先抓取远端更新。", token);
        var localRef = await RunAsync(root, ["show-ref", "--verify", "--quiet", "refs/heads/" + localBranch], ReadTimeout, token);
        if (localRef.Success) throw new StudioXException("GIT_BRANCH_EXISTS", "同名本地分支已经存在；请切换该分支或选择新名称。");
        if (localRef.ExitCode != 1 || localRef.TimedOut) throw CommandError("检查本地分支", localRef);
        var result = await RunAsync(root, ["switch", "--no-guess", "-c", localBranch, "--track", remoteRef], WriteTimeout, token);
        if (!result.Success) throw WorkingTreeChangeError("检出远端分支", result);
    }

    public async Task SetUpstreamAsync(string directory, string remote, string localBranch,
        string remoteBranch, CancellationToken token = default)
    {
        var root = await RequireRepositoryAsync(directory, token);
        await RequireRemoteAsync(root, remote, token);
        await RequireLocalBranchAsync(root, localBranch, token);
        await RequireBranchNameAsync(root, remoteBranch, token);
        var remoteRef = $"refs/remotes/{remote}/{remoteBranch}";
        await RequireRefAsync(root, remoteRef, "远端跟踪分支尚不存在；请先抓取远端更新。", token);
        Checked(await RunAsync(root, ["branch", "--set-upstream-to=" + remoteRef, localBranch], WriteTimeout, token),
            "设置上游分支");
    }

    private async Task RequireRefAsync(string root, string fullRef, string missingMessage, CancellationToken token)
    {
        var result = await RunAsync(root, ["show-ref", "--verify", "--quiet", fullRef], ReadTimeout, token);
        if (result.Success) return;
        if (result.ExitCode == 1 && !result.TimedOut) throw new StudioXException("GIT_REMOTE_BRANCH", missingMessage);
        throw CommandError("检查远端分支", result);
    }

    private async Task<IReadOnlyList<string>> RemoteNamesAsync(string root, CancellationToken token) =>
        Checked(await RunAsync(root, ["remote"], ReadTimeout, token), "读取远端列表")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private async Task<string> RemoteUrlAsync(string root, string remote, bool push, CancellationToken token)
    {
        if (!ValidRemoteName(remote)) throw new StudioXException("GIT_REMOTE", "远端名称无效。");
        var args = push ? new[] { "remote", "get-url", "--push", "--all", remote }
            : new[] { "remote", "get-url", "--all", remote };
        var result = await RunAsync(root, args, ReadTimeout, token);
        if (!result.Success || result.OutputTruncated)
            throw new StudioXException("GIT_REMOTE", "无法读取远端地址；请检查仓库配置。Git 原始输出已隐藏以保护凭据。");
        var urls = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (urls.Length != 1)
            throw new StudioXException("GIT_REMOTE", "该远端配置了多个地址；图形模式只支持单一地址，避免意外操作多个仓库。");
        return urls[0];
    }

    private async Task<ProcessResult> RunNetworkAsync(string root, IReadOnlyList<string> args,
        string remoteUrl, string? selectedAccount, CancellationToken token)
    {
        if (ContainsEmbeddedCredentials(remoteUrl))
            throw new StudioXException("GIT_REMOTE_CREDENTIALS", "远端地址内含账号或凭据；请改为不带凭据的 HTTPS 地址，并通过 GitHub 登录授权。");
        ValidateRemoteUrl(remoteUrl);
        return await RunAsync(root, args, NetworkTimeout, token,
            githubCredentialUrl: IsGitHubHttpsUrl(remoteUrl) ? remoteUrl : null, selectedAccount: selectedAccount);
    }

    // GCM 官方建议用 credential.<URL>.username 选定同一主机上的账号；命令级覆盖仅对本次操作有效。
    internal IReadOnlyList<string> GitHubCredentialCommandArguments(string remoteUrl, string? selectedAccount)
    {
        var helperArgs = GitHubCredentialConfigArguments(remoteUrl);
        if (selectedAccount is null) return helperArgs;
        if (selectedAccount.Length is < 1 or > 39 || !char.IsAsciiLetterOrDigit(selectedAccount[0])
            || !selectedAccount.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new StudioXException("GITHUB_ACCOUNT_NAME", "GitHub 账号名只能由英文字母、数字、短横线或下划线组成，且长度不超过 39 个字符。");
        return [.. helperArgs, "-c", $"credential.{remoteUrl.TrimEnd('/')}.username={selectedAccount}"];
    }

    private static void NetworkChecked(ProcessResult result, string operation)
    {
        if (result.Success && !result.OutputTruncated) return;
        throw NetworkError(operation, result);
    }

    private static StudioXException NetworkError(string operation, ProcessResult result)
    {
        if (result.TimedOut) return new("GIT_TIMEOUT", $"Git {operation}超时；请检查网络连接后重试。");
        if (result.OutputTruncated) return new("GIT_OUTPUT_LIMIT", $"Git {operation}输出超过限制；操作状态不确定，请刷新仓库状态。");
        var diagnostic = result.StandardError + "\n" + result.StandardOutput;
        if (ContainsAny(diagnostic, "non-fast-forward", "fetch first", "failed to push some refs", "[rejected]"))
            return new("GIT_NON_FAST_FORWARD", $"Git {operation}被远端拒绝：远端已有新提交。请先抓取并处理分歧，再重试推送。");
        if (ContainsAny(diagnostic, "authentication failed", "permission denied", "could not read username", "invalid username or token", "http 401", "http 403"))
            return new("GIT_AUTH", $"Git {operation}未通过认证或没有仓库权限；请检查 GitHub 登录及仓库访问权限。");
        if (ContainsAny(diagnostic, "repository not found", "does not appear to be a git repository", "not found", "http 404"))
            return new("GIT_REMOTE_NOT_FOUND", $"Git {operation}找不到远端仓库；请检查地址与账号访问权限。");
        if (ContainsAny(diagnostic, "could not resolve host", "couldn't connect", "failed to connect", "network is unreachable", "ssl certificate", "schannel"))
            return new("GIT_NETWORK", $"Git {operation}无法连接远端；请检查网络、代理与证书设置。");
        return new("GIT_NETWORK", $"Git {operation}失败（退出码 {result.ExitCode}）。请检查仓库地址、登录状态及访问权限；原始网络输出已隐藏以保护凭据。");
    }

    private static StudioXException WorkingTreeChangeError(string operation, ProcessResult result)
    {
        if (result.TimedOut) return new("GIT_TIMEOUT", $"Git {operation}超时；请刷新工作区状态。");
        if (ContainsAny(result.StandardError + result.StandardOutput, "would be overwritten", "local changes", "untracked working tree files"))
            return new("GIT_WORKTREE_CONFLICT", $"Git {operation}未完成：本地未提交文件与目标分支冲突，工作区文件已保留。请先处理这些文件后重试。");
        return new("GIT_COMMAND", $"Git {operation}未完成；请刷新 Git 图谱检查分支和工作区。原始输出已隐藏以保护凭据。");
    }

    private static bool ContainsAny(string text, params string[] fragments) =>
        fragments.Any(fragment => text.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static bool IsGitHubHttpsUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsGitHubRemoteUrl(string url) => IsGitHubHttpsUrl(url)
        || url.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase)
        || (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "ssh"
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.UserInfo == "git");

    private static bool ContainsEmbeddedCredentials(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.IsFile) return false;
        if (uri.Scheme is "http" or "https")
            return uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0;
        if (uri.Scheme == "ssh")
            return uri.UserInfo.Contains(':') || uri.Query.Length > 0 || uri.Fragment.Length > 0;
        return false;
    }

    private static string DisplayRemoteUrl(string url)
    {
        if (Path.IsPathFullyQualified(url)) return url;
        if (Regex.IsMatch(url, @"^git@[A-Za-z0-9.-]+:[A-Za-z0-9._/-]+$", RegexOptions.CultureInvariant))
            return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "（不支持的远端地址，已隐藏）";
        if (uri.Scheme is not ("http" or "https" or "ssh")) return "（不支持的远端地址，已隐藏）";
        var display = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
        return display.Uri.AbsoluteUri;
    }

    private static void ValidateRemoteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 4096 || url != url.Trim()
            || url.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new StudioXException("GIT_REMOTE", "远端地址无效。");
        if (Path.IsPathFullyQualified(url)) return; // 本地 bare 仓库可用于离线协作与验收。
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0 && uri.UserInfo.Length == 0
                && uri.Query.Length == 0 && uri.Fragment.Length == 0) return;
            if (uri.Scheme == "ssh" && uri.Host.Length > 0 && (uri.UserInfo is "" or "git")
                && uri.Query.Length == 0 && uri.Fragment.Length == 0
                && Regex.IsMatch(uri.AbsolutePath, @"^/[A-Za-z0-9._/-]+$", RegexOptions.CultureInvariant)) return;
        }
        // GitHub 常用 SSH 写法。禁止任意外部传输协议与参数形式。
        if (Regex.IsMatch(url, @"^git@[A-Za-z0-9.-]+:[A-Za-z0-9._/-]+$", RegexOptions.CultureInvariant)) return;
        throw new StudioXException("GIT_REMOTE", "只支持无内嵌凭据的 HTTPS、Git SSH 或本地绝对路径远端地址。");
    }
}
