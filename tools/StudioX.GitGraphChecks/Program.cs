using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;
using System.Reflection;

var source = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var runtime = Path.Combine(source, "artifacts", "git-runtime");
var git = new GitRepositoryService(runtime);
var graph = new GitGraphService(git);
var output = Path.Combine(source, ".artifacts", "git-graph-" + DateTime.Now.ToString("yyyyMMdd-HHmmssfff"));
var repository = Path.Combine(output, "中文 repo");
Directory.CreateDirectory(repository);
var checks = 0;
void Check(bool ok, string label)
{
    if (!ok) throw new Exception(label);
    checks++;
    Console.WriteLine("PASS " + label);
}
async Task<string> Run(string cwd, params string[] command)
{
    var result = await new ProcessRunner().RunAsync(new(git.Executable, command, cwd, TimeSpan.FromSeconds(30),
        git.Environment(), RemoveEnvironment: GitRepositoryService.AmbientVariables));
    if (!result.Success) throw new Exception(string.Join(' ', command) + "\n" + result.StandardOutput + result.StandardError);
    return result.StandardOutput.Trim();
}

await git.InitializeAsync(repository);
Check((await graph.GetLocalIdentityAsync(repository)).Name is null, "new repository has no local identity");
await graph.SetLocalIdentityAsync(repository, "Graph Test", "graph@example.invalid");
Check((await graph.GetLocalIdentityAsync(repository)).Email == "graph@example.invalid", "identity is saved in local config");
var empty = await graph.GetSnapshotAsync(repository);
Check(empty.HeadCommit is null && empty.Commits.Count == 0 && empty.CurrentBranch == "main", "unborn main is visible");

var filename = "中文 空格.txt";
await File.WriteAllTextAsync(Path.Combine(repository, filename), "第一行\n");
var untracked = await graph.GetSnapshotAsync(repository);
Check(untracked.WorkingFiles.Count == 1 && untracked.WorkingFiles[0].Path == filename && untracked.WorkingFiles[0].IsUntracked,
    "porcelain -z preserves Unicode and spaces");
Check((await graph.GetDiffAsync(repository, GitDiffTarget.WorkingTree, filename)).Text.Contains("+第一行", StringComparison.Ordinal),
    "untracked file has preview diff");
await graph.StageAsync(repository, [filename]);
Check((await graph.GetSnapshotAsync(repository)).WorkingFiles.Single().IndexStatus == 'A', "stage is visible");
await graph.UnstageAsync(repository, [filename]);
Check((await graph.GetSnapshotAsync(repository)).WorkingFiles.Single().IsUntracked, "unstage works before first commit");
await graph.StageAsync(repository, [filename]);
await graph.CommitAsync(repository, "初始提交");
var first = (await graph.GetSnapshotAsync(repository)).HeadCommit!;
Check(first.Length == 40 && (await graph.GetCommitDetailsAsync(repository, first)).Files.Single().Path == filename,
    "root commit details list its files");
Check((await graph.GetDiffAsync(repository, GitDiffTarget.Commit, filename, first)).Text.Contains("+第一行", StringComparison.Ordinal),
    "root commit diff is available");
await Run(repository, "tag", "-a", "v1", "-m", "first release", first);
Check((await graph.GetSnapshotAsync(repository)).Refs.Any(r => r.Kind == GitRefKind.Tag && r.Name == "v1" && r.Hash == first),
    "annotated tag resolves to its commit");

await File.AppendAllTextAsync(Path.Combine(repository, filename), "第二行\n");
Check((await graph.GetDiffAsync(repository, GitDiffTarget.WorkingTree, filename)).Text.Contains("+第二行", StringComparison.Ordinal),
    "working tree diff");
await graph.StageAsync(repository, [filename]);
Check((await graph.GetDiffAsync(repository, GitDiffTarget.Staged, filename)).Text.Contains("+第二行", StringComparison.Ordinal),
    "staged diff");
await graph.CommitAsync(repository, "second");
var second = (await graph.GetSnapshotAsync(repository)).HeadCommit!;
Check((await graph.CompareCommitsAsync(repository, first, second, filename)).Text.Contains("+第二行", StringComparison.Ordinal),
    "compare two commits");
Check((await graph.GetSnapshotAsync(repository, 1)).HasMoreCommits, "bounded history reports more rows");
var renamed = "改名 文件.txt";
File.Move(Path.Combine(repository, filename), Path.Combine(repository, renamed));
await graph.StageAsync(repository, [filename, renamed]);
var stagedRename = (await graph.GetSnapshotAsync(repository)).WorkingFiles.Single();
Check(stagedRename.Path == renamed && stagedRename.OriginalPath == filename && stagedRename.IndexStatus == 'R',
    "staged rename preserves both Unicode paths");
await graph.CommitAsync(repository, "rename");
Check((await graph.GetCommitDetailsAsync(repository, (await graph.GetSnapshotAsync(repository)).HeadCommit!)).Files
    .Any(f => f.Path == renamed && f.OriginalPath == filename && f.Status.StartsWith('R')),
    "commit details preserve rename origin");

await graph.CreateBranchAsync(repository, "feature", checkout: true);
await File.WriteAllTextAsync(Path.Combine(repository, "feature.txt"), "feature\n");
await graph.StageAsync(repository, ["feature.txt"]);
await graph.CommitAsync(repository, "feature commit");
var feature = await graph.GetSnapshotAsync(repository);
Check(feature.CurrentBranch == "feature" && feature.Refs.Any(r => r.Name == "feature" && r.IsCurrent), "branch creation and current ref");
await graph.CheckoutBranchAsync(repository, "main");
await graph.MergeAsync(repository, "feature");
await graph.DeleteBranchAsync(repository, "feature");
Check(!(await graph.GetSnapshotAsync(repository)).Refs.Any(r => r.Name == "feature"), "merged branch deletes safely");
await graph.CreateBranchAsync(repository, "unmerged", first, checkout: true);
await File.WriteAllTextAsync(Path.Combine(repository, "unmerged.txt"), "unmerged\n");
await graph.StageAsync(repository, ["unmerged.txt"]);
await graph.CommitAsync(repository, "unmerged commit");
await graph.CheckoutBranchAsync(repository, "main");
try { await graph.DeleteBranchAsync(repository, "unmerged"); throw new Exception("unsafe branch deletion succeeded"); }
catch (StudioXException ex) when (ex.Code == "GIT_COMMAND") { Check(true, "unmerged branch cannot be deleted by GUI"); }
try { await graph.StageAsync(repository, ["../outside.txt"]); throw new Exception("escaping path accepted"); }
catch (StudioXException ex) when (ex.Code == "GIT_PATH") { Check(true, "path traversal is rejected"); }

var remote = Path.Combine(output, "remote repo.git");
await Run(output, "init", "--bare", "--initial-branch=main", remote);
var githubUrl = "https://github.com/example/repo.git";
var accountArguments = (IReadOnlyList<string>)typeof(GitRepositoryService)
    .GetMethod("GitHubCredentialCommandArguments", BindingFlags.Instance | BindingFlags.NonPublic)!
    .Invoke(git, [githubUrl, "Selected-Account_1"])!;
await Run(repository, "config", "--local", "credential.https://github.com/example/repo.git.username", "other-account");
Check(await Run(repository, [.. accountArguments, "config", "--get-urlmatch", "credential.username", githubUrl])
    == "Selected-Account_1", "selected GitHub account overrides URL-specific config for one command");
Check(await Run(repository, "config", "--get-urlmatch", "credential.username", githubUrl) == "other-account",
    "selected GitHub account is not persisted in repository config");
try { await graph.CloneAsync(githubUrl, Path.Combine(output, "invalid account clone"), "bad\nname");
    throw new Exception("unsafe account name accepted"); }
catch (StudioXException ex) when (ex.Code == "GITHUB_ACCOUNT_NAME")
{ Check(!Directory.Exists(Path.Combine(output, "invalid account clone")), "unsafe account name is rejected before GitHub network access"); }
Check((await graph.GetRemotesAsync(repository)).Count == 0, "new repository has no remotes");
try { await graph.FetchAsync(repository); throw new Exception("fetch without a remote succeeded"); }
catch (StudioXException ex) when (ex.Code == "GIT_REMOTE") { Check(true, "fetch explains when no remote is connected"); }
await graph.AddRemoteAsync(repository, "origin", remote);
await graph.FetchAsync(repository, "origin", "ignored for local path");
Check(true, "selected GitHub account does not affect local bare remotes");
Check((await graph.GetRemotesAsync(repository)).SequenceEqual(["origin"]), "remote is saved in local config");
Check((await graph.GetRemoteDetailsAsync(repository)).Single().FetchUrl == remote,
    "remote details expose the local bare repository");
try { await graph.AddRemoteAsync(repository, "--all", remote); throw new Exception("option-like remote name accepted"); }
catch (StudioXException ex) when (ex.Code == "GIT_REMOTE") { Check(true, "option-like remote names are rejected"); }
try { await graph.AddRemoteAsync(repository, "credential", "https://user:secret@github.com/example/repo.git"); throw new Exception("credential URL accepted"); }
catch (StudioXException ex) when (ex.Code == "GIT_REMOTE") { Check(true, "embedded credentials are rejected for new remotes"); }
await graph.PublishBranchAsync(repository, "origin", "main");
Check((await graph.GetSnapshotAsync(repository)).Upstream == "origin/main", "first publish sets upstream");
Check(await Run(remote, "for-each-ref", "--format=%(refname)", "refs/heads") == "refs/heads/main",
    "first publish updates only the selected branch");
var peer = Path.Combine(output, "peer");
Check(await graph.CloneAsync(remote, peer) == peer, "clone into a new target directory");
try { await graph.CloneAsync(remote, peer); throw new Exception("existing clone target accepted"); }
catch (StudioXException ex) when (ex.Code == "GIT_CLONE_EXISTS") { Check(true, "clone preserves an existing target directory"); }
var failedTarget = Path.Combine(output, "failed clone");
try { await graph.CloneAsync(Path.Combine(output, "missing.git"), failedTarget); throw new Exception("missing remote cloned"); }
catch (StudioXException ex) when (ex.Code is "GIT_REMOTE_NOT_FOUND" or "GIT_NETWORK")
{
    Check(!Directory.Exists(failedTarget) && !Directory.EnumerateDirectories(output, ".studiox-clone-*").Any(),
        "failed clone leaves no destination or staging directory");
}
await Run(peer, "config", "user.name", "Peer Test");
await Run(peer, "config", "user.email", "peer@example.invalid");
Check((await graph.GetRemoteBranchesAsync(peer)).Single().Name == "main", "clone creates a remote tracking branch");
await File.WriteAllTextAsync(Path.Combine(peer, "remote.txt"), "remote\n");
await Run(peer, "add", "remote.txt");
await Run(peer, "commit", "-m", "remote commit");
await Run(peer, "push");
await graph.FetchAsync(repository, "origin");
var behind = await graph.GetSnapshotAsync(repository);
Check(behind.Upstream == "origin/main" && behind.Behind == 1 && behind.Refs.Any(r => r.Kind == GitRefKind.RemoteBranch),
    "fetch refreshes remote refs and behind count");
await graph.PullAsync(repository);
Check((await graph.GetSnapshotAsync(repository)).Behind == 0 && File.Exists(Path.Combine(repository, "remote.txt")),
    "pull advances by fast-forward");
await File.WriteAllTextAsync(Path.Combine(repository, "local.txt"), "local\n");
await graph.StageAsync(repository, ["local.txt"]);
await graph.CommitAsync(repository, "local commit");
await Run(repository, "config", "push.default", "matching");
await graph.PushAsync(repository);
Check(await Run(remote, "rev-parse", "main") == (await graph.GetSnapshotAsync(repository)).HeadCommit, "push updates local bare remote");
Check(await Run(remote, "for-each-ref", "--format=%(refname)", "refs/heads") == "refs/heads/main",
    "push ignores broad push.default and updates only upstream");
await Run(repository, "config", "remote.origin.mirror", "true");
try { await graph.PushAsync(repository); throw new Exception("mirror remote was pushed"); }
catch (StudioXException ex) when (ex.Code == "GIT_MIRROR") { Check(true, "mirror push is blocked in GUI"); }
await Run(repository, "config", "--unset", "remote.origin.mirror");
await Run(peer, "switch", "-c", "feature");
await File.WriteAllTextAsync(Path.Combine(peer, "peer-feature.txt"), "feature\n");
await Run(peer, "add", "peer-feature.txt");
await Run(peer, "commit", "-m", "peer feature");
await Run(peer, "push", "--set-upstream", "origin", "feature");
await graph.FetchAsync(repository, "origin");
Check((await graph.GetRemoteBranchesAsync(repository, "origin")).Any(b => b.Name == "feature"),
    "fetch exposes a peer branch");
await graph.CheckoutRemoteBranchAsync(repository, "origin", "feature", "review-feature");
Check((await graph.GetSnapshotAsync(repository)).Upstream == "origin/feature", "remote checkout tracks peer branch");
await graph.CheckoutBranchAsync(repository, "main");
await graph.SetUpstreamAsync(repository, "origin", "main", "main");
Check((await graph.GetSnapshotAsync(repository)).Upstream == "origin/main", "explicit upstream binding");
await Run(peer, "switch", "main");
await Run(peer, "pull", "--ff-only");
await File.WriteAllTextAsync(Path.Combine(peer, "peer-diverged.txt"), "peer\n");
await Run(peer, "add", "peer-diverged.txt");
await Run(peer, "commit", "-m", "peer diverged");
await Run(peer, "push");
await File.WriteAllTextAsync(Path.Combine(repository, "local-diverged.txt"), "local\n");
await graph.StageAsync(repository, ["local-diverged.txt"]);
await graph.CommitAsync(repository, "local diverged");
await Run(repository, "config", "pull.rebase", "true");
var divergentHead = (await graph.GetSnapshotAsync(repository)).HeadCommit;
try { await graph.PullAsync(repository); throw new Exception("divergent pull rewrote history"); }
catch (StudioXException ex) when (ex.Code == "GIT_DIVERGED")
{
    var divergent = await graph.GetSnapshotAsync(repository);
    Check(divergent.HeadCommit == divergentHead && divergent.Ahead == 1 && divergent.Behind == 1,
        "pull rejects divergence despite pull.rebase=true");
}
try { await graph.PushAsync(repository); throw new Exception("non-fast-forward push succeeded"); }
catch (StudioXException ex) when (ex.Code == "GIT_NON_FAST_FORWARD")
{ Check(!ex.Message.Contains(remote, StringComparison.OrdinalIgnoreCase), "rejected push gives safe diagnostics"); }

await Run(repository, "remote", "set-url", "origin", "https://user:secret@github.com/example/repo.git");
var redacted = (await graph.GetRemoteDetailsAsync(repository)).Single();
Check(redacted.HasEmbeddedCredentials && !redacted.FetchUrl.Contains("secret", StringComparison.Ordinal)
    && !redacted.PushUrl.Contains("secret", StringComparison.Ordinal), "remote details never expose URL credentials");
try { await graph.FetchAsync(repository, "origin"); throw new Exception("embedded credential URL fetched"); }
catch (StudioXException ex) when (ex.Code == "GIT_REMOTE_CREDENTIALS")
{ Check(!ex.Message.Contains("secret", StringComparison.Ordinal), "credential URL cannot enter network path"); }
await graph.SetRemoteUrlAsync(repository, "origin", remote);
Check((await graph.GetRemoteDetailsAsync(repository)).Single().FetchUrl == remote,
    "remote URL can be restored safely");
await Run(repository, "remote", "set-url", "--push", "origin", "https://user:secret@github.com/example/repo.git");
var pushWithCredential = (await graph.GetRemoteDetailsAsync(repository)).Single();
Check(pushWithCredential.FetchUrl == remote && pushWithCredential.HasEmbeddedCredentials
    && !pushWithCredential.PushUrl.Contains("secret", StringComparison.Ordinal),
    "an existing credential push URL is redacted independently from fetch URL");
try { await graph.PushAsync(repository); throw new Exception("credential push URL reached network"); }
catch (StudioXException ex) when (ex.Code == "GIT_REMOTE_CREDENTIALS")
{ Check(true, "credential push URL is blocked before network access"); }
try { await graph.SetRemotePushUrlAsync(repository, "origin", "https://user:new-secret@github.com/example/repo.git");
    throw new Exception("credential replacement push URL accepted"); }
catch (StudioXException ex) when (ex.Code == "GIT_REMOTE")
{ Check(true, "replacement push URL cannot embed credentials"); }
await graph.SetRemotePushUrlAsync(repository, "origin", remote);
Check((await graph.GetRemoteDetailsAsync(repository)).Single() is { FetchUrl: var fetchUrl, PushUrl: var pushUrl }
    && fetchUrl == remote && pushUrl == remote, "push URL can be repaired without changing fetch URL");
await Run(repository, "remote", "set-url", "--add", "--push", "origin", Path.Combine(output, "other.git"));
try { await graph.SetRemotePushUrlAsync(repository, "origin", remote); throw new Exception("multiple push URLs changed"); }
catch (StudioXException ex) when (ex.Code == "GIT_REMOTE")
{ Check(true, "multiple push URLs require explicit command-line resolution"); }
await Run(repository, "config", "--local", "--unset-all", "remote.origin.pushurl");

var conflictClient = Path.Combine(output, "conflict client");
await graph.CloneAsync(remote, conflictClient);
var conflictingPath = Path.Combine(conflictClient, "remote.txt");
await File.WriteAllTextAsync(conflictingPath, "my uncommitted LCD fix\n");
var conflictHead = (await graph.GetSnapshotAsync(conflictClient)).HeadCommit;
await File.WriteAllTextAsync(Path.Combine(peer, "remote.txt"), "peer's incompatible change\n");
await Run(peer, "add", "remote.txt");
await Run(peer, "commit", "-m", "conflicting peer edit");
await Run(peer, "push");
try { await graph.PullAsync(conflictClient); throw new Exception("dirty conflict was overwritten"); }
catch (StudioXException ex) when (ex.Code == "GIT_WORKTREE_CONFLICT")
{
    Check(await File.ReadAllTextAsync(conflictingPath) == "my uncommitted LCD fix\n"
        && (await graph.GetSnapshotAsync(conflictClient)).HeadCommit == conflictHead,
        "pull conflict preserves uncommitted file and HEAD");
}

var nested = Path.Combine(repository, "nested");
Directory.CreateDirectory(nested);
try { await graph.GetSnapshotAsync(nested); throw new Exception("parent repository was accepted"); }
catch (StudioXException ex) when (ex.Code == "GIT_NOT_PROJECT_REPOSITORY") { Check(true, "parent repository cannot be modified accidentally"); }

Console.WriteLine($"PASS: {checks} Git graph checks. Artifacts: {output}");
