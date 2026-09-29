namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Application;
using StudioX.Application.Editing;
using StudioX.Application.Mcp;
using StudioX.Foundation;

public partial class MainWindow
{
    private AgentEditorSession? agentEditorSession;
    private AgentChangesView? agentChangesView;
    private TabItem? agentChangesTab;
    private AgentAccessMode agentAccessMode;
    private bool changingAgentAccess;
    private string? agentAccessProject;
    private Task agentAccessSaveTask = Task.CompletedTask;
    private int agentAccessChoice;
    private AgentAccessService AgentAccess => new(services.DataDirectory);

    private async Task LoadAgentAccessAsync(string project, int generation)
    {
        agentAccessMode = AgentAccessMode.Review;
        agentAccessProject = null;
        var choice = agentAccessChoice;
        var mode = await AgentAccess.LoadAsync(project);
        if (!IsCurrentAiEditorProject(project, generation) || choice != agentAccessChoice)
        {
            return;
        }
        agentAccessProject = project;
        agentAccessMode = mode;
        changingAgentAccess = true;
        AgentAccessPicker.SelectedIndex = (int)mode;
        changingAgentAccess = false;
        UpdateAgentAccessHint();
    }
    private async void AgentAccess_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (changingAgentAccess || AgentAccessHint is null || projectDirectory is not { } project || AgentAccessPicker.SelectedIndex < 0)
        {
            return;
        }
        var generation = aiProjectGeneration;
        var mode = (AgentAccessMode)AgentAccessPicker.SelectedIndex;
        var choice = ++agentAccessChoice;
        var previous = agentAccessMode;
        agentAccessMode = mode;
        agentAccessProject = project;
        UpdateAgentAccessHint();
        try
        {
            // 点击选项就是用户的模式选择；不再弹第二次确认。
            var prior = agentAccessSaveTask;
            async Task SaveChoiceAsync()
            {
                try
                {
                    await prior;
                }
                catch { /* 前一次失败已显示，后一次选择仍可重试保存。 */ }
                await AgentAccess.SaveAsync(project, mode);
            }
            agentAccessSaveTask = SaveChoiceAsync();
            await agentAccessSaveTask;
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            if (choice == agentAccessChoice && IsCurrentAiEditorProject(project, generation))
            {
                agentAccessMode = previous;
                changingAgentAccess = true;
                AgentAccessPicker.SelectedIndex = (int)previous;
                changingAgentAccess = false;
                UpdateAgentAccessHint();
                AiStatus.Text = "保存授权模式失败：" + ex.Message;
            }
        }
    }
    private void UpdateAgentAccessHint() => AgentAccessHint.Text = agentAccessMode switch
    {
        AgentAccessMode.FullAuthorization => "工程内编辑、保存、构建和本地 Git 自动执行；设备、外部目录和远端操作仍询问。",
        AgentAccessMode.FullAccess => "现有全部 Agent 工具自动批准，含设备、外部目录和 Git 远端；仍检查目标、路径和文件冲突。",
        _ => "修改和有副作用的操作逐次确认；可在 AI 修改页勾选修改块。"
    };
    private AgentEditorSession CreateAgentEditorSession(string project, int generation, IStudioXMcpAuthorizer authorizer)
    {
        void Current(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!IsCurrentAiEditorProject(project, generation))
            {
                throw new StudioXException("AGENT_PROJECT_CHANGED", "工程会话已变化。");
            }
        }
        var access = new DesktopAgentEditorAccess(
            token => Dispatcher.InvokeAsync(() =>
            {
                Current(token);
                CaptureEditorView();
                return new AgentEditorSnapshot(activeEditor?.Source.RelativePath, activeEditor?.CaretOffset ?? 0,
                activeEditor?.SelectionStart ?? 0, activeEditor?.SelectionLength ?? 0, CaptureWorkspaceBuffers());
            }).Task,
            (plan, token) => Dispatcher.InvokeAsync(() => { Current(token); ShowAgentChanges(plan); }).Task,
            (plan, token) => Dispatcher.InvokeAsync<IReadOnlyList<WorkspaceFileChange>>(() => { Current(token); return agentChangesView!.Selected(plan); }).Task,
            (changes, token) => Dispatcher.InvokeAsync(async () =>
            {
                Current(token);
                if (services.Debugger.IsActive)
                {
                    throw new StudioXException("AGENT_DEBUG_ACTIVE", "请先结束调试再修改源码。");
                }
                var captured = CaptureWorkspaceBuffers();
                await services.WorkspaceEdits.ValidateAsync(project, changes, captured, token);
                foreach (var change in changes)
                {
                    await services.LocalHistory.CaptureAsync(project, change.Path, change.Before, "AI 修改前", token);
                }
                await services.WorkspaceEdits.ValidateAsync(project, changes, captured, token);
                Current(token);
                if (captured.Any(b => FindEditor(b.Source.RelativePath)?.Buffer.Text != b.Text) || changes.Any(c => FindEditor(c.Path) is { } open && open.Buffer.Text != c.Before))
                {
                    throw new StudioXException("AGENT_EDIT_STALE", "审阅期间代码变化，所有文件均未修改。");
                }
                var prepared = changes.Select(c => (Change: c, Editor: FindEditor(c.Path) ?? AddEditor(c.Source))).ToArray();
                foreach (var (change, document) in prepared)
                {
                    using (document.Buffer.RunUpdate())
                    {
                        foreach (var match in change.Matches.OrderByDescending(m => m.Start))
                        {
                            document.Buffer.Replace(match.Start, match.Length, match.Replacement);
                        }
                    }
                }
                QueueLiveDiagnostics();
                QueueEditorCheckpoint();
            }).Task.Unwrap(),
            (snapshots, token) => Dispatcher.InvokeAsync(async () =>
            {
                Current(token);
                foreach (var snapshot in snapshots)
                {
                    var current = FindEditor(snapshot.Source.RelativePath);
                    var disk = await services.Files.ReadAsync(project, snapshot.Source.RelativePath, token);
                    if (current is null || current.Buffer.Text != snapshot.Text || disk.DiskHash != snapshot.Source.DiskHash)
                    {
                        throw new StudioXException("AGENT_SAVE_STALE", "保存前代码或磁盘已变化，未启动编译。");
                    }
                }
                Current(token);
                foreach (var snapshot in snapshots)
                {
                    var current = FindEditor(snapshot.Source.RelativePath);
                    if (current is null || current.Buffer.Text != snapshot.Text)
                    {
                        throw new StudioXException("AGENT_SAVE_STALE", "保存过程中编辑内容变化，未启动编译。");
                    }
                    var saved = await services.Files.SaveAsync(project, snapshot.Source, snapshot.Text, token);
                    // 保存成功后即记录新磁盘基线，取消不能让已落盘内容仍背着旧哈希。
                    if (IsCurrentAiEditorProject(project, generation))
                    {
                        current.Source = saved;
                        UpdateEditorHeader(current);
                        QueuePluginDocumentEvent("document.saved", current);
                        QueueEditorCheckpoint();
                    }
                    Current(token);
                }
                QueueEditorCheckpoint();
            }).Task.Unwrap());
        var session = new AgentEditorSession(services, project, access, authorizer);
        session.Changed += () => Dispatcher.BeginInvoke(() => { if (agentEditorSession == session && IsCurrentAiEditorProject(project, generation)) { agentChangesView?.Refresh(session); } });
        return session;
    }
    private void ShowAgentChanges(AgentEditPlan? plan = null)
    {
        if (agentEditorSession is null)
        {
            AiStatus.Text = "当前会话尚无 AI 修改。";
            return;
        }
        if (agentChangesTab is null || !WorkspaceTabs.Items.Contains(agentChangesTab))
        {
            agentChangesView = new AgentChangesView
            {
                CanAct = () => aiCancellation is null,
                ApplyRequested = id => RunAsync(async token => { if (agentEditorSession is { } session) { await session.ApplyAsync(id, token, userSelectedApply: true); } }),
                UndoRequested = id => RunAsync(async token => { if (agentEditorSession is { } session) { await session.UndoAsync(id, token); } }),
                UndoTaskRequested = id => RunAsync(async token => { if (agentEditorSession is { } session) { await session.UndoTaskAsync(id, token); } })
            };
            agentChangesTab = AddToolTab("AI 修改", agentChangesView);
        }
        agentChangesView!.Refresh(agentEditorSession, plan);
        ShowDocument(agentChangesTab);
    }
    private void AgentChanges_Click(object sender, RoutedEventArgs e) => ShowAgentChanges();
    private void ClearAgentWorkspace()
    {
        agentEditorSession = null;
        agentAccessMode = AgentAccessMode.Review;
        agentAccessProject = null;
        changingAgentAccess = true;
        AgentAccessPicker.SelectedIndex = 0;
        changingAgentAccess = false;
        UpdateAgentAccessHint();
        if (agentChangesTab is not null)
        {
            WorkspaceTabs.Items.Remove(agentChangesTab);
        }
        agentChangesTab = null;
        agentChangesView = null;
    }
}
