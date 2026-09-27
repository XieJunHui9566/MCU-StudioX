namespace StudioX.Application.Mcp;

/// <summary>没有交互授权宿主时默认拒绝产生副作用的操作。</summary>
public sealed class DenyStudioXMcpAuthorizer : IStudioXMcpAuthorizer
{
    public Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token) =>
        Task.FromResult(false);
}
