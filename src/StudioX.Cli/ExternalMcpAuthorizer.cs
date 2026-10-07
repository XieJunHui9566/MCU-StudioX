namespace StudioX.Cli;

using StudioX.Application.Mcp;

/// <summary>启动 CLI MCP 即建立当前工程的外部 Agent 会话；不使用交互弹窗，保留逐次范围校验与审计。</summary>
internal sealed class ExternalMcpAuthorizer(string projectDirectory) : IStudioXMcpAuthorizer
{
    private readonly string project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));

    public Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // 会话只能授权启动时绑定的工程；具体路径、文件哈希、设备和发送参数仍由工具核验。
        if (!Path.IsPathFullyQualified(request.Project) ||
            !Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Project)).Equals(project, StringComparison.OrdinalIgnoreCase) ||
            !Enum.IsDefined(request.Permission))
        {
            return Task.FromResult(false);
        }
        // stdout 属于 MCP 协议；审计只能进入 stderr，避免破坏 JSON-RPC 帧。
        Console.Error.WriteLine($"MCP_SESSION_AUTHORIZED {request.Permission} {request.Tool}");
        return Task.FromResult(true);
    }
}
