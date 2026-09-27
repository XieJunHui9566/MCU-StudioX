namespace StudioX.Engine;

public sealed record GitGraphSnapshot(string RepositoryDirectory, string CurrentBranch, bool DetachedHead, string? HeadCommit,
    IReadOnlyList<GitCommit> Commits, IReadOnlyList<GitRef> Refs, IReadOnlyList<GitWorkingFile> WorkingFiles,
    string? Upstream, int Ahead, int Behind, bool HasMoreCommits);
