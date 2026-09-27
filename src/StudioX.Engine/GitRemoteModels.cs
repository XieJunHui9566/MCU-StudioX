namespace StudioX.Engine;

/// <summary>显示用远端信息。地址在离开 Engine 前会移除内嵌凭据与查询参数。</summary>
public sealed record GitRemoteInfo(string Name, string FetchUrl, string PushUrl,
    bool IsGitHub, bool HasEmbeddedCredentials);
