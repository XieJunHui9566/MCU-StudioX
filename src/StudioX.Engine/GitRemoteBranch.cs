namespace StudioX.Engine;

/// <summary>远端跟踪分支是最近一次 fetch 的本地副本，不表示服务器实时状态。</summary>
public sealed record GitRemoteBranch(string Remote, string Name, string FullName, string Hash, bool IsUpstream);
