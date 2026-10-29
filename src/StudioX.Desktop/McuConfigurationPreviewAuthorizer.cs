namespace StudioX.Desktop;

using StudioX.Application.Mcp;

/// <summary>仅用于显式预览模式的新夹具；限定绝对工程及唯一配置文件，不授权任何设备工具。</summary>
internal sealed class McuConfigurationPreviewAuthorizer(string project) : IStudioXMcpAuthorizer
{
    public Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(request.Project == project && request.Tool == "project_edit_file" &&
            request.Permission == StudioXMcpPermission.FileWrite && request.Summary.Contains("device.mcuconfig", StringComparison.Ordinal));
    }
}
