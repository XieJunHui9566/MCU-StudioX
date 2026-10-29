namespace StudioX.Engine;

/// <summary>一次已获得构建互斥权的请求；后端负责自己的工具内容锁和产物失效策略。</summary>
internal sealed record BuildBackendRequest(string Root, ProjectManifest Project, bool ConfigureOnly,
    IProgress<string>? Progress, IProgress<string>? Output, CancellationToken Token);
