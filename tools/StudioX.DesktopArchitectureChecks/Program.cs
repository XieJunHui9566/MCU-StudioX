using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Desktop;

internal static class Program
{
    private static int checks;

    [STAThread]
    private static void Main()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        CodeHighlightingChecks.Run(Check);
        SafeTextEditorChecks.Run(Check);
        BuildMemoryViewChecks.Run(Check);
        CheckEditorSynchronization();
        CheckActivityAndUsage();
        CheckApprovalLifetime();
        SynchronizationContext.SetSynchronizationContext(null);
        CheckConversationsAsync().GetAwaiter().GetResult();
        Console.WriteLine($"PASS {checks} desktop responsibility checks; no hardware or API was accessed.");
    }

    private static void CheckEditorSynchronization()
    {
        var source = new SourceDocument("src/main.c", "old", Encoding.UTF8, "hash-old", false);
        var session = new EditorDocumentSession(source);
        var captures = 0;
        var restores = 0;
        var changes = 0;
        session.Changed = (_, _) => changes++;
        session.Buffer.TextChanged += session.Changed;
        var synchronizer = new EditorDocumentSynchronizer(_ => true, () => captures++, _ => { }, _ => restores++);
        var disk = source with
        {
            Text = "new",
            DiskHash = "hash-new"
        };
        Check(synchronizer.Apply(session, disk) == EditorDiskSyncResult.Updated &&
            session.Buffer.Text == "new" && !session.IsDirty && changes == 0 && captures == 1 && restores == 1,
            "磁盘变化更新干净缓冲区，保护编辑事件与活动视图");
        session.Buffer.Insert(0, "user ");
        Check(changes == 1 && session.Buffer.UndoStack.CanUndo, "磁盘同步后仍保留正常编辑和撤销能力");
        Check(synchronizer.Apply(session, disk with
        {
            Text = "third",
            DiskHash = "hash-third"
        }) ==
            EditorDiskSyncResult.UnsavedChangesPreserved && session.Buffer.Text == "user new" &&
            session.Source.DiskHash == "hash-new" && session.Buffer.UndoStack.CanUndo,
            "用户未保存改动与旧哈希基线保留，后续保存可拒绝外部覆盖");
        Check(synchronizer.Apply(session, disk) == EditorDiskSyncResult.Unchanged && captures == 1,
            "重复落盘通知不清空撤销记录或重设视图");
        Check(AiWorkspaceChangePolicy.MayChangeWorkspace(new("1", "git_branch", "{\"action\":\"switch\"}")) &&
            !AiWorkspaceChangePolicy.MayChangeWorkspace(new("2", "git_branch", "{\"action\":\"list\"}")) &&
            AiWorkspaceChangePolicy.MayChangeWorkspace(new("3", "git_remote", "{\"action\":\"pull\"}")) &&
            !AiWorkspaceChangePolicy.MayChangeWorkspace(new("4", "git_remote", "invalid")),
            "Git 切换和拉取复查磁盘，查询与无效参数不触发写入同步");
    }

    private static void CheckActivityAndUsage()
    {
        var items = new StackPanel();
        var transcript = new ScrollViewer { Content = items };
        var completed = 0;
        var activity = new AiActivityPresenter(items, transcript, () => { }, _ => completed++);
        activity.Start();
        var firstProgress = activity.Progress!;
        firstProgress.Report(new(AiAgentProgressKind.ReasoningDelta, 1, Text: "received"));
        firstProgress.Report(new(AiAgentProgressKind.ToolCallCompleted, 1, Text: "src/main.c", ToolName: "project_edit_file"));
        activity.DrainProgress();
        Check(activity.HasReceivedContent && completed == 1 && items.Children.Count == 1,
            "活动视图展示收到的内容，并立即转发成功工具写入事件");
        activity.RetainInterrupted("stopped");
        Check(activity.Row is null && activity.Progress is null && items.Children.Count == 1,
            "中断保留已收到的活动卡，关闭后台缓冲区和计时器");
        firstProgress.Report(new(AiAgentProgressKind.ToolCallCompleted, 1, ToolName: "old"));
        activity.Start();
        activity.DrainProgress();
        Check(!activity.HasReceivedContent && completed == 1 && items.Children.Count == 2,
            "下一轮活动不接受已关闭请求的延迟事件");
        activity.Finish();
        Check(items.Children.Count == 1 && activity.Progress is null,
            "完成只移除当前活动气泡，保留之前的中断记录");

        var arc = new System.Windows.Shapes.Path();
        var full = new System.Windows.Shapes.Ellipse();
        var badge = new Grid();
        var cache = new TextBlock();
        var meter = new AiUsageMeterPresenter(arc, full, badge, cache);
        meter.Update(new(), 100, true, 75, 25);
        Check(cache.Text.Contains("75 / 100", StringComparison.Ordinal) && cache.Text.Contains("75%", StringComparison.Ordinal),
            "缓存比例来自 API 命中和未命中实数");
        meter.Update(new(), 100, true, 75, null);
        Check(cache.Text.Contains("未返回未命中量", StringComparison.Ordinal) && !cache.Text.Contains('%'),
            "缺少缓存字段时不推算命中率");
        meter.Update(new(), 100, true, 0, 0);
        Check(cache.Text.Contains("0 / 0", StringComparison.Ordinal) && !cache.Text.Contains('%'),
            "缓存零总量不显示错误百分比");
    }

    private static void CheckApprovalLifetime()
    {
        var owner = new Grid();
        owner.Resources["PrimaryButton"] = new Style(typeof(Button));
        var items = new StackPanel();
        var transcript = new ScrollViewer { Content = items };
        var current = true;
        var approvals = new AiApprovalPresenter(owner, items, transcript, () => Task.CompletedTask,
            (_, _) => current, () => false, _ => { }, _ => { }, () => { });
        var request = new StudioXMcpApprovalRequest("project", "project_edit_file", "write file", StudioXMcpPermission.FileWrite);
        var allowTask = approvals.RequestAsync(request, 1, CancellationToken.None);
        Check(items.Children.Count == 1, "待授权时显示核对卡");
        ApprovalButton(items, "允许本次").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(WaitOnDispatcher(allowTask) && items.Children.Count == 0, "批准后立即移除授权卡");
        using var cancellation = new CancellationTokenSource();
        var cancelTask = approvals.RequestAsync(request, 1, cancellation.Token);
        cancellation.Cancel();
        Check(!WaitOnDispatcher(cancelTask) && items.Children.Count == 0, "请求取消后拒绝并移除授权卡");
        current = false;
        Check(!WaitOnDispatcher(approvals.RequestAsync(request, 1, CancellationToken.None)) && items.Children.Count == 0,
            "旧工程请求不能生成授权卡");
    }

    private static Button ApprovalButton(StackPanel items, string label)
    {
        var card = (Border)items.Children[^1];
        var body = (StackPanel)card.Child;
        var actions = (StackPanel)body.Children[^1];
        return actions.Children.OfType<Button>().Single(button => Equals(button.Content, label));
    }

    private static T WaitOnDispatcher<T>(Task<T> task)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        return task.GetAwaiter().GetResult();
    }

    private static async Task CheckConversationsAsync()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "studiox-desktop-architecture-" + Guid.NewGuid().ToString("N"));
        var firstProject = Path.Combine(fixture, "first-project");
        var secondProject = Path.Combine(fixture, "second-project");
        Directory.CreateDirectory(firstProject);
        Directory.CreateDirectory(secondProject);
        try
        {
            var conversations = new AiConversationController(new(Path.Combine(fixture, "user-data")));
            var generation = conversations.BindProject(firstProject);
            var first = await conversations.CreateAsync(firstProject, generation);
            conversations.Select(first, "");
            var second = await conversations.CreateAsync(firstProject, generation);
            Check(conversations.Select(second, "first draft") == "" &&
                conversations.Select(first, "second draft") == "first draft" &&
                conversations.Select(second, "changed first") == "second draft", "切换对话分别保存和恢复草稿");
            var turn = new AiAgentTurn("question", "answer", SteeringMessages: ["adjust"]);
            var reply = new AiAgentReply("answer", [turn], new(100, 10, 110),
                AggregateUsage: new(500, 30, 530, 400, 100, 3, 3));
            var saved = await conversations.SaveReplyAsync(firstProject, generation, second, "question", turn, reply);
            Check(saved.Error is null && conversations.History.Single().SteeringMessages?.Single() == "adjust" &&
                conversations.Active?.LastPromptTokens == 100 && conversations.Active.LastPromptCacheHitTokens == 400,
                "保存答复保留方向注入、最近上下文量和整轮 API 缓存量");
            await conversations.DeleteAsync(firstProject, generation, saved.Conversation);
            var failed = await conversations.SaveReplyAsync(firstProject, generation, saved.Conversation, "retry", turn, reply);
            Check(failed.Error is not null && conversations.SaveFailed && conversations.History.Count == 2,
                "保存失败保留内存答复并禁止继续覆盖历史");
            var newGeneration = conversations.BindProject(secondProject);
            Check(newGeneration > generation && conversations.Active is null && conversations.History.Count == 0 &&
                !conversations.SaveFailed && conversations.Select(null, "", projectChanged: true) == "",
                "切换工程清空本工程视图状态和草稿");
            Check(!await conversations.LoadAsync(firstProject, generation) && conversations.Conversations.Count == 0,
                "迟到的旧工程历史不能覆盖新工程");
            Check(await conversations.LoadAsync(secondProject, newGeneration) && conversations.Conversations.Count == 0,
                "新工程只恢复自己的历史");
        }
        finally
        {
            // 只删除本次创建并已确认位于临时目录中的独立测试目录。
            var resolved = Path.GetFullPath(fixture);
            var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("测试目录不在临时目录中，拒绝清理。");
            }
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException(description);
        }
        checks++;
        Console.WriteLine("PASS " + description);
    }
}
