namespace StudioX.Application.Mcp;

/// <summary>一次工具调用的授权请求；只读目录授权另外保存在当前会话内。</summary>
public sealed record StudioXMcpApprovalRequest(string Project, string Tool, string Summary,
    StudioXMcpPermission Permission);
