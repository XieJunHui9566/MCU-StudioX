namespace StudioX.Desktop;

using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.Editing;
using StudioX.Application.Mcp;

public partial class MainWindow
{
    public async Task ShowAgentWorkspacePreviewAsync(string fixture)
    {
        await OpenProjectAsync(fixture, CancellationToken.None);
        await OpenSourceAsync("src/main.c", CancellationToken.None);
        await ShowAiSidebarAsync();
        _ = await GetAiMcpSessionAsync(fixture, aiProjectGeneration, CancellationToken.None);
        agentEditorSession!.BeginTask("离线交互验收 · 逐块审阅与任务撤销");
        await agentEditorSession.PlanAsync([new("src/main.c", AgentEditorSession.Hash(activeEditor!.Buffer.Text),
            [new("= 1;", "= 2;"), new("helper();", "helper() + 1;")])], "调整初值和返回值");
        AiStatus.Text = "离线测试工程：未调用在线模型或硬件。";
    }

    public async Task RenderAgentWorkspacePreviewAsync(string directory, string fixture)
    {
        var checks = new List<string>();
        void Check(bool condition, string text)
        {
            if (!condition)
            {
                throw new InvalidOperationException(text);
            }
            checks.Add(text);
        }
        await ShowAgentWorkspacePreviewAsync(fixture);
        var session = agentEditorSession!;
        var document = FindEditor("src/main.c")!;
        var original = document.Buffer.Text;
        var plan = session.Plans.Single();
        AgentAccessPicker.SelectedIndex = (int)AgentAccessMode.FullAuthorization;
        await agentAccessSaveTask;
        Check(await AgentAccess.LoadAsync(fixture) == AgentAccessMode.FullAuthorization, "picker persists project authorization");
        var count = AiTranscriptItems.Children.Count;
        await session.ApplyAsync(plan.Id);
        Check(plan.Status == "applied" && document.Buffer.Text.Contains("= 2;"), "actual desktop host applies dirty buffer changes without confirmation card");
        Check(AiTranscriptItems.Children.Count == count, "automatic editing adds no approval card");
        Check(await File.ReadAllTextAsync(Path.Combine(fixture, "src/main.c")) == original, "review apply keeps disk unchanged");
        session.RecordValidation(true, false);
        document.Buffer.Insert(document.Buffer.TextLength, "// later manual change\n");
        Check(session.ValidationStatus.Contains("过期"), "real buffer event invalidates compiled evidence");
        await session.UndoTaskAsync(session.TaskId);
        Check(document.Buffer.Text == original + "// later manual change\n", "whole task undo preserves subsequent user edits in desktop buffers");
        AgentAccessPicker.SelectedIndex = (int)AgentAccessMode.FullAccess;
        await agentAccessSaveTask;
        foreach (var permission in Enum.GetValues<StudioXMcpPermission>())
        {
            Check(await RequestAiMcpApprovalAsync(new(fixture, "test_authorization_only", "只验证授权分类，不执行工具", permission), aiProjectGeneration, CancellationToken.None), "full-access approval route: " + permission);
        }
        Check(AiTranscriptItems.Children.Count == count, "full access adds no approval cards for existing permission categories");
        await LoadAgentAccessAsync(fixture, aiProjectGeneration);
        Check(AgentAccessPicker.SelectedIndex == (int)AgentAccessMode.FullAccess, "loading project restores visible mode selection");
        AgentAccessPicker.SelectedIndex = (int)AgentAccessMode.FullAuthorization;
        await agentAccessSaveTask;
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = RequestAiMcpApprovalAsync(new(fixture, "test_hardware_gate_only", "只测试卡片，不连接设备", StudioXMcpPermission.HardwareConnect), aiProjectGeneration, cancellation.Token);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Check(!pending.IsCompleted && AiTranscriptItems.Children.Count == count + 1, "project authorization still asks for hardware access");
            cancellation.Cancel();
            Check(!await pending && AiTranscriptItems.Children.Count == count, "cancel removes pending approval and denies operation");
        }
        AgentAccessPicker.SelectedIndex = (int)AgentAccessMode.Review;
        await agentAccessSaveTask;
        document.Buffer.Text = original;
        session.BeginTask("示例任务 · 两个可选修改块");
        await session.PlanAsync([new("src/main.c", AgentEditorSession.Hash(original), [new("= 1;", "= 2;"), new("helper();", "helper() + 1;")])], "调整初值和返回值");
        Width = 1380;
        Height = 920;
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(this, Path.Combine(directory, "agent-changes-" + theme.Id + ".png"));
        }
        ApplyTheme(ThemeService.Dark);
        Width = 1100;
        Height = 760;
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Render(this, Path.Combine(directory, "agent-compact.png"));
        Check(AgentAccessPicker.IsVisible && AgentAccessPicker.ActualWidth > 100 && agentChangesView!.ActualWidth > 350 && agentChangesView.RowDefinitions[2].ActualHeight > 100, "mode picker and stacked review fit compact window");
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS " + checks.Count + "\n" + string.Join('\n', checks));
    }
}
