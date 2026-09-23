namespace StudioX.Engine;

/// <summary>图形视图的引用类型；远端分支只是本地跟踪引用，不表示远端实时状态。</summary>
public enum GitRefKind { LocalBranch, RemoteBranch, Tag }

public sealed record GitCommit(string Hash, IReadOnlyList<string> Parents, string Subject, string Author, DateTimeOffset AuthoredAt);

public sealed record GitRef(string Name, string FullName, string Hash, GitRefKind Kind, bool IsCurrent);

/// <summary>Git porcelain v1 的两个状态位原样保留；问号表示未跟踪。</summary>
public sealed record GitWorkingFile(string Path, string? OriginalPath, char IndexStatus, char WorkTreeStatus, bool IsUntracked);

public sealed record GitGraphSnapshot(string RepositoryDirectory, string CurrentBranch, bool DetachedHead, string? HeadCommit,
    IReadOnlyList<GitCommit> Commits, IReadOnlyList<GitRef> Refs, IReadOnlyList<GitWorkingFile> WorkingFiles,
    string? Upstream, int Ahead, int Behind, bool HasMoreCommits);

public sealed record GitChangedFile(string Path, string? OriginalPath, string Status);

public sealed record GitCommitDetails(string Hash, string Subject, string Message, string Author, string AuthorEmail,
    DateTimeOffset AuthoredAt, string Committer, string CommitterEmail, DateTimeOffset CommittedAt,
    IReadOnlyList<GitChangedFile> Files);

public enum GitDiffTarget { WorkingTree, Staged, Commit }

public sealed record GitDiffResult(string Text, bool Truncated);

/// <summary>仅表示此仓库的 local config；为空时 Git 可能仍有用户的全局身份。</summary>
public sealed record GitIdentity(string? Name, string? Email);
