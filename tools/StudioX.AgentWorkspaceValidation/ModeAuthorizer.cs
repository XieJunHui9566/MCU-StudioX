using StudioX.Application;
using StudioX.Application.Mcp;

internal sealed class ModeAuthorizer : IStudioXMcpAuthorizer
{
    public AgentAccessMode Mode { get; set; } = AgentAccessMode.FullAuthorization;
    public int Prompts
    {
        get; private set;
    }
    public Action<StudioXMcpApprovalRequest>? OnApproval
    {
        get; set;
    }
    public Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        OnApproval?.Invoke(request);
        if (AgentAccessService.Allows(Mode, request.Permission))
        {
            return Task.FromResult(true);
        }
        Prompts++;
        return Task.FromResult(false);
    }
}
