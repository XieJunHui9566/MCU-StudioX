namespace StudioX.Desktop;

using System.Windows;
using StudioX.Application;
using StudioX.Application.Mcp;

public partial class MainWindow
{
    private AiApprovalPresenter? aiApprovalPresenter;

    private Task<bool> RequestAiMcpApprovalAsync(
        StudioXMcpApprovalRequest request, int generation, CancellationToken token)
    {
        if (!token.IsCancellationRequested && IsCurrentAiEditorProject(request.Project, generation) &&
            string.Equals(agentAccessProject, request.Project, StringComparison.OrdinalIgnoreCase) &&
            AgentAccessService.Allows(agentAccessMode, request.Permission))
        {
            SetAiActivityStatus($"{(agentAccessMode == AgentAccessMode.FullAccess ? "完全访问" : "全面授权")} · 正在执行 {request.Tool}…");
            Log($"AI 自动授权 [{agentAccessMode}] {request.Tool} / {request.Permission} · {request.Project}");
            return Task.FromResult(true);
        }
        aiApprovalPresenter ??= new(this, AiTranscriptItems, AiTranscript,
            () => AiSidebar.Visibility == Visibility.Visible ? Task.CompletedTask : ShowAiSidebarAsync(),
            IsCurrentAiEditorProject, () => closing || closed,
            SetAiActivityStatus, message => AiStatus.Text = message, UpdateAiEmptyHint);
        return aiApprovalPresenter.RequestAsync(request, generation, token);
    }
}
