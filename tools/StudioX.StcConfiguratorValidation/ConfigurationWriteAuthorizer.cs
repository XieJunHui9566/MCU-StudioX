namespace StudioX.StcConfiguratorValidation;

using StudioX.Application.Mcp;

/// <summary>只授权本次隔离夹具中的单配置文件保存，绝不授权设备或其它工具。</summary>
internal sealed class ConfigurationWriteAuthorizer : IStudioXMcpAuthorizer
{
    public bool Allow
    {
        get; set;
    }
    public int Requests
    {
        get; private set;
    }
    public Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Requests++;
        return Task.FromResult(Allow && request.Tool == "project_edit_file" && request.Permission == StudioXMcpPermission.FileWrite && request.Summary.Contains("device.mcuconfig", StringComparison.Ordinal));
    }
}
