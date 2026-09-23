namespace StudioX.Engine;

/// <summary>显示用远端信息。地址在离开 Engine 前会移除内嵌凭据与查询参数。</summary>
public sealed record GitRemoteInfo(string Name, string FetchUrl, string PushUrl,
    bool IsGitHub, bool HasEmbeddedCredentials);

/// <summary>远端跟踪分支是最近一次 fetch 的本地副本，不表示服务器实时状态。</summary>
public sealed record GitRemoteBranch(string Remote, string Name, string FullName, string Hash, bool IsUpstream);
