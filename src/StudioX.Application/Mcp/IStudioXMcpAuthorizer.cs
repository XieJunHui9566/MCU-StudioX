namespace StudioX.Application.Mcp;

/// <summary>由宿主展示权限及操作摘要，工具不能自行扩大或复用写入授权。</summary>
public interface IStudioXMcpAuthorizer
{
    Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token);
}
