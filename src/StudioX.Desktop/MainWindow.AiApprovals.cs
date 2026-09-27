namespace StudioX.Desktop;

using System.Windows;
using StudioX.Application.Mcp;

public partial class MainWindow
{
    private AiApprovalPresenter? aiApprovalPresenter;

    private Task<bool> RequestAiMcpApprovalAsync(
        StudioXMcpApprovalRequest request, int generation, CancellationToken token)
    {
        aiApprovalPresenter ??= new(this, AiTranscriptItems, AiTranscript,
            () => AiSidebar.Visibility == Visibility.Visible ? Task.CompletedTask : ShowAiSidebarAsync(),
            IsCurrentAiEditorProject, () => closing || closed,
            SetAiActivityStatus, message => AiStatus.Text = message, UpdateAiEmptyHint);
        return aiApprovalPresenter.RequestAsync(request, generation, token);
    }
}
