namespace StudioX.Desktop;

using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Foundation;

public partial class MainWindow
{
    private IReadOnlyList<AiAgentTurn> aiHistory = [];
    private IReadOnlyList<AiConversation> aiConversations = [];
    private AiConversation? aiActiveConversation;
    private CancellationTokenSource? aiCancellation;
    private AiAgentSteeringQueue? aiSteering;
    private readonly ConcurrentQueue<(int Generation, string Message)> aiAppliedSteering = new();
    private AiSettings? aiDisplaySettings;
    private Task aiThinkingSaveTask = Task.CompletedTask;
    private int aiThinkingSaveGeneration;
    private int aiProjectGeneration;
    private bool aiConversationPickerUpdating;
    private bool aiThinkingSliderUpdating;
    private bool aiConversationLoading;
    private bool aiConversationOperationBusy;
    private bool aiHistorySaveFailed;
    private readonly Dictionary<string, string> aiDrafts = new(StringComparer.Ordinal);
    private bool aiCollapsedOutline;
    private double savedAiSidebarWidth = 360;
    private StudioXMcpSession? aiMcpSession;
    private Task aiMcpActiveCall = Task.CompletedTask;
    private Task aiMcpDisposalTask = Task.CompletedTask;

    private async Task<StudioXMcpSession> GetAiMcpSessionAsync(string project, int generation, CancellationToken token)
    {
        await aiMcpDisposalTask;
        token.ThrowIfCancellationRequested();
        if (generation != aiProjectGeneration ||
            !string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase))
            throw new OperationCanceledException("AI 工程已经切换。", token);
        if (aiMcpSession is { } existing &&
            string.Equals(existing.Project, project, StringComparison.OrdinalIgnoreCase)) return existing;
        var authorizer = new DesktopMcpAuthorizer(this, candidate =>
            !closing && !closed && string.Equals(projectDirectory, candidate, StringComparison.OrdinalIgnoreCase),
            (request, approvalToken) => RequestAiMcpApprovalAsync(request, generation, approvalToken));
        var tools = new StudioXMcpTools(services, project, authorizer, AiHasUnsavedDocumentsAsync);
        StudioXMcpSession created;
        try { created = await StudioXMcpSession.CreateAsync(tools, token); }
        catch { await tools.DisposeAsync(); throw; }
        if (generation != aiProjectGeneration ||
            !string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase))
        {
            await created.DisposeAsync();
            throw new OperationCanceledException("AI 工程已经切换。", token);
        }
        aiMcpSession = created;
        return created;
    }

    private Task<bool> AiHasUnsavedDocumentsAsync() => Dispatcher.CheckAccess()
        ? Task.FromResult(editorDocuments.Any(session => session.IsDirty))
        : Dispatcher.InvokeAsync(() => editorDocuments.Any(session => session.IsDirty)).Task;

    private void QueueAiMcpSessionDisposal()
    {
        var session = aiMcpSession;
        aiMcpSession = null;
        if (session is null) return;
        var previous = aiMcpDisposalTask;
        var activeCall = aiMcpActiveCall;
        aiMcpDisposalTask = DisposeAiMcpSessionAfterAsync(previous, activeCall, session);
    }

    private async Task DisposeAiMcpSessionAfterAsync(Task previous, Task activeCall, StudioXMcpSession session)
    {
        try { await previous; }
        catch (Exception ex) { Log("上一 AI MCP 会话清理失败：" + ex); }
        try { await activeCall; }
        catch (Exception) { /* 请求错误已在 AI 对话中呈现；仍需释放设备会话。 */ }
        try { await session.DisposeAsync(); }
        catch (Exception ex) { Log("AI MCP 会话清理失败：" + ex); }
    }

    private async Task DisposeAiMcpSessionAsync()
    {
        aiCancellation?.Cancel();
        QueueAiMcpSessionDisposal();
        await aiMcpDisposalTask;
    }

    private async void AiAssistant_Click(object sender, RoutedEventArgs e)
    {
        if (AiSidebar.Visibility == Visibility.Visible) { HideAiSidebar(); return; }
        await ShowAiSidebarAsync();
    }

    private async void AiSettingsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (aiCancellation is not null || aiConversationOperationBusy) { Status.Text = "请先停止当前 AI 请求。"; return; }
        CloseAiPopups();
        try
        {
            await aiThinkingSaveTask;
            var settings = await services.AiSettings.LoadAsync();
            var dialog = new AiSettingsWindow(services.AiSettings, services.AiCredentials, services.WebCredentials, settings) { Owner = this };
            dialog.ShowDialog();
            await RefreshAiConfigurationAsync();
        }
        catch (Exception ex) { Status.Text = "打开 AI 接口设置失败：" + ex.Message; }
    }

    private void AiClose_Click(object sender, RoutedEventArgs e) => HideAiSidebar();

    private void CloseAiPopups()
    {
        AiHistoryPopup.IsOpen = false;
        AiModelPopup.IsOpen = false;
    }

    private void AiHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        AiModelPopup.IsOpen = false;
        AiHistoryPopup.IsOpen = !AiHistoryPopup.IsOpen;
        if (AiHistoryPopup.IsOpen) AiHistoryPopup.Child?.Focus();
    }

    private void AiModelButton_Click(object sender, RoutedEventArgs e)
    {
        AiHistoryPopup.IsOpen = false;
        AiModelPopup.IsOpen = !AiModelPopup.IsOpen;
        if (AiModelPopup.IsOpen) AiModelPopup.Child?.Focus();
    }

    private void AiPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseAiPopups();
        AiPromptInput.Focus();
        e.Handled = true;
    }

    private async Task ShowAiSidebarAsync()
    {
        if (AiSidebar.Visibility != Visibility.Visible)
        {
            AiSidebarColumn.Width = new GridLength(savedAiSidebarWidth);
            AiSidebarSplitterColumn.Width = new GridLength(5);
            AiSidebar.Visibility = AiSidebarSplitter.Visibility = Visibility.Visible;
            if (OutlinePanel.Visibility == Visibility.Visible)
            {
                SetOutlineVisible(false);
                aiCollapsedOutline = true;
            }
            AiToggleButton.ToolTip = "收起右侧 AI 助手";
            System.Windows.Automation.AutomationProperties.SetName(AiToggleButton, "收起右侧 AI 助手");
        }
        await RefreshAiConfigurationAsync();
        if (AiPromptPanel.Visibility == Visibility.Visible) AiPromptInput.Focus();
    }

    private async Task RefreshAiConfigurationAsync()
    {
        try
        {
            var settings = await services.AiSettings.LoadAsync();
            aiDisplaySettings = settings;
            var configured = services.AiCredentials.HasApiKey(settings.BaseUrl);
            AiPopupModelName.Text = settings.Model;
            AiTranscript.Visibility = Visibility.Visible;
            AiEmptyHint.Visibility = configured && AiTranscriptItems.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AiUnconfiguredHint.Text = "未配置 API";
            AiUnconfiguredHint.Visibility = !configured && AiTranscriptItems.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AiStatus.Visibility = Visibility.Visible;
            AiPromptPanel.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
            aiThinkingSliderUpdating = true;
            AiThinkingSlider.Value = settings.ReasoningEffort switch
            {
                "none" => 0, "low" => 1, "max" => 3, _ => 2
            };
            aiThinkingSliderUpdating = false;
            UpdateAiThinkingLabel();
            var reasoningSupported = AiSettingsService.SupportsReasoningEffort(settings);
            AiThinkingSlider.IsEnabled = configured && reasoningSupported && aiCancellation is null;
            AiThinkingHint.Text = reasoningSupported ? "关闭     ·     低     ·     高     ·     最大" : "当前接口未声明思考强度支持";
            UpdateAiContextMeter(settings, aiActiveConversation?.LastPromptTokens,
                aiActiveConversation is { Turns.Count: > 0 },
                aiActiveConversation?.LastPromptCacheHitTokens,
                aiActiveConversation?.LastPromptCacheMissTokens);
            if (!configured) AiStatus.Text = "请先保存 API Key；当前工程的历史对话仍可查看。";
            UpdateAiConversationButtons();
        }
        catch (Exception ex)
        {
            aiDisplaySettings = null;
            AiTranscript.Visibility = AiStatus.Visibility = Visibility.Visible;
            AiEmptyHint.Visibility = AiPromptPanel.Visibility = Visibility.Collapsed;
            AiUnconfiguredHint.Text = "API 设置不可用";
            AiUnconfiguredHint.Visibility = AiTranscriptItems.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Status.Text = "读取 AI 设置失败：" + ex.Message;
            UpdateAiConversationButtons();
        }
    }

    private void HideAiSidebar()
    {
        if (AiSidebar.Visibility != Visibility.Visible) return;
        CloseAiPopups();
        savedAiSidebarWidth = Math.Clamp(AiSidebarColumn.ActualWidth, 320, 620);
        AiSidebar.Visibility = AiSidebarSplitter.Visibility = Visibility.Collapsed;
        AiSidebarColumn.Width = AiSidebarSplitterColumn.Width = new GridLength(0);
        AiToggleButton.ToolTip = "打开右侧 AI 助手";
        System.Windows.Automation.AutomationProperties.SetName(AiToggleButton, "打开右侧 AI 助手");
        if (aiCollapsedOutline && OutlinePanel.Visibility == Visibility.Collapsed) SetOutlineVisible(true);
        aiCollapsedOutline = false;
    }

    private async void AiSend_Click(object sender, RoutedEventArgs e)
    {
        if (aiCancellation is not null)
        {
            QueueAiSteeringFromComposer();
            return;
        }
        if (aiConversationLoading || aiConversationOperationBusy || aiHistorySaveFailed) return;
        if (projectDirectory is not { } project) { AiStatus.Text = "请先创建或打开工程。"; return; }
        if (editorDocuments.Any(session => session.IsDirty))
        {
            AiStatus.Text = "编辑器中有未保存的文件。请先保存，使 AI 读取到当前代码。";
            return;
        }
        var prompt = AiPromptInput.Text.Trim();
        if (prompt.Length == 0) { AiStatus.Text = "请输入问题或任务。"; return; }
        if (prompt.Length > 4_000) { AiStatus.Text = "单次提问不能超过 4,000 字符。"; return; }
        var generation = aiProjectGeneration;
        AiSettings settings;
        AiConversation conversation;
        aiConversationOperationBusy = true;
        CloseAiPopups();
        UpdateAiConversationButtons();
        try
        {
            await aiThinkingSaveTask;
            settings = await services.AiSettings.LoadAsync();
            if (generation != aiProjectGeneration ||
                !string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase)) return;
            if (!services.AiCredentials.HasApiKey(settings.BaseUrl))
            {
                await RefreshAiConfigurationAsync();
                return;
            }
            conversation = aiActiveConversation ?? await services.AiConversations.CreateAsync(project);
            if (generation != aiProjectGeneration ||
                !string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase)) return;
            if (aiActiveConversation is null)
            {
                aiConversations = [conversation, .. aiConversations];
                SetActiveAiConversation(conversation);
                aiDrafts.Remove(AiDraftKey(null));
                RefreshAiConversationPicker();
            }
        }
        catch (Exception ex)
        {
            if (generation == aiProjectGeneration) AiStatus.Text = "无法开始 AI 请求：" + ex.Message;
            return;
        }
        finally
        {
            aiConversationOperationBusy = false;
            UpdateAiConversationButtons();
        }

        using var cancellation = new CancellationTokenSource();
        var steering = new AiAgentSteeringQueue();
        steering.MessageDequeued += message =>
        {
            aiAppliedSteering.Enqueue((generation, message));
            _ = Dispatcher.BeginInvoke(DrainAiAppliedSteering);
        };
        aiCancellation = cancellation;
        aiSteering = steering;
        UpdateAiConversationButtons();
        AiThinkingSlider.IsEnabled = false;
        AiCancelButton.IsEnabled = true;
        AiCancelButton.Visibility = Visibility.Visible;
        AiPromptInput.Clear();
        var pendingBubble = AppendAiTranscript("你", prompt);
        StartAiActivity();
        AiStatus.Text = "AI 正在处理；当前阶段与工具调用会显示在对话中。";
        try
        {
            var mcpSession = await GetAiMcpSessionAsync(project, generation, cancellation.Token);
            var agent = services.CreateAiAgent(settings, mcpSession);
            SetAiActivityStatus("工程工具已就绪，正在请求模型…");
            var sendTask = agent.SendAsync(project, prompt, aiHistory, cancellation.Token, aiProgressBuffer,
                steering);
            aiMcpActiveCall = sendTask;
            var reply = await sendTask;
            if (generation != aiProjectGeneration ||
                !string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase)) return;
            DrainAiActivityProgress();
            DrainAiAppliedSteering();
            SetAiActivityStatus("正在刷新工程状态并保存对话…");
            var turn = reply.History.LastOrDefault() ?? throw new InvalidOperationException("AI 没有返回本轮对话记录。");
            await aiEditorSyncTask;
            if (generation != aiProjectGeneration ||
                !string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase)) return;
            var workspaceNeedsReview = await RefreshAiWorkspaceAfterToolsAsync(project, turn, generation);
            if (generation != aiProjectGeneration ||
                !string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase)) return;
            var updated = conversation with
            {
                Title = conversation.Turns.Count == 0 ? AiConversationTitle(prompt) : conversation.Title,
                Turns = [.. conversation.Turns, turn],
                LastPromptTokens = reply.Usage?.PromptTokens,
                LastCompletionTokens = reply.Usage?.CompletionTokens,
                LastPromptCacheHitTokens = reply.AggregateUsage?.PromptCacheHitTokens,
                LastPromptCacheMissTokens = reply.AggregateUsage?.PromptCacheMissTokens
            };
            Exception? saveError = null;
            try
            {
                updated = await services.AiConversations.SaveAsync(project, updated);
                if (generation != aiProjectGeneration) return;
                ReplaceAiConversation(updated);
            }
            catch (Exception ex)
            {
                saveError = ex;
                if (generation == aiProjectGeneration) aiHistorySaveFailed = true;
            }
            if (generation != aiProjectGeneration) return;
            aiActiveConversation = updated;
            AiConversationHeading.Text = updated.Title;
            aiHistory = updated.Turns;
            FinishAiActivity();
            AppendAiTranscript("AI", reply.Text, turn);
            UpdateAiContextMeter(settings, reply.Usage?.PromptTokens, hasRequest: true,
                reply.AggregateUsage?.PromptCacheHitTokens,
                reply.AggregateUsage?.PromptCacheMissTokens);
            AiStatus.Text = saveError is not null
                ? "回答已显示，但对话历史保存失败：" + saveError.Message + " 请复制所需内容并新建对话。"
                : "已完成。" + AiCacheStatus(reply.AggregateUsage);
            if (workspaceNeedsReview) AiStatus.Text += " 编辑器中有后续未保存的修改，已保留；请核对磁盘文件。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (generation == aiProjectGeneration)
            {
                DrainAiActivityProgress();
                DrainAiAppliedSteering();
                await aiEditorSyncTask;
                if (aiActivityToolCount > 0 || aiReceivedReasoning.Length > 0 || aiReceivedAnswer.Length > 0)
                    RetainInterruptedAiActivity("请求已停止；上方保留本轮已收到的内容与工具记录，此轮未存入历史。");
                else
                {
                    FinishAiActivity();
                    AiTranscriptItems.Children.Remove(pendingBubble);
                }
                RestoreAiComposerAfterInterrupted(prompt, steering);
                AiStatus.Text = "AI 请求已停止，可修改问题后重试。";
                UpdateAiEmptyHint();
            }
        }
        catch (Exception ex)
        {
            if (generation == aiProjectGeneration)
            {
                DrainAiActivityProgress();
                DrainAiAppliedSteering();
                if (ex is StudioXException { Code: "AI_RESPONSE_FORMAT" } &&
                    ex.Message.StartsWith("模型把工具调用写成了普通文本", StringComparison.Ordinal))
                    ClearAiAnswerPreview();
                await aiEditorSyncTask;
                if (aiActivityToolCount > 0 || aiReceivedReasoning.Length > 0 || aiReceivedAnswer.Length > 0)
                    RetainInterruptedAiActivity("请求失败；上方保留本轮已收到的内容与工具记录，此轮未存入历史。");
                else
                {
                    FinishAiActivity();
                    AiTranscriptItems.Children.Remove(pendingBubble);
                }
                RestoreAiComposerAfterInterrupted(prompt, steering);
                AiStatus.Text = "AI 请求失败：" + ex.Message;
                UpdateAiEmptyHint();
            }
        }
        finally
        {
            var unconsumed = steering.DrainPending();
            if (generation == aiProjectGeneration && unconsumed.Count > 0)
                RestoreAiPendingSteering(unconsumed);
            if (ReferenceEquals(aiSteering, steering)) aiSteering = null;
            if (ReferenceEquals(aiCancellation, cancellation)) aiCancellation = null;
            if (generation == aiProjectGeneration)
            {
                AiCancelButton.IsEnabled = false;
                AiCancelButton.Visibility = Visibility.Collapsed;
                AiThinkingSlider.IsEnabled = AiSettingsService.SupportsReasoningEffort(settings);
                UpdateAiSteeringBanner();
            }
            UpdateAiConversationButtons();
        }
    }

    private void AiCancel_Click(object sender, RoutedEventArgs e) => aiCancellation?.Cancel();

    private void QueueAiSteeringFromComposer()
    {
        var text = AiPromptInput.Text.Trim();
        if (text.Length == 0) { AiStatus.Text = "请输入要调整的方向。"; return; }
        if (text.Length > 4_000) { AiStatus.Text = "单次提示不能超过 4,000 字符。"; return; }
        if (aiSteering?.TryEnqueue(text) != true)
        {
            AiStatus.Text = "这一轮已经结束；请将提示作为下一条消息发送。";
            return;
        }
        AiPromptInput.Clear();
        UpdateAiSteeringBanner();
        AiStatus.Text = "已排队：当前模型响应和已开始的工具调用结束后会应用调整方向。";
    }

    private void AiSteeringRemove_Click(object sender, RoutedEventArgs e)
    {
        if (aiSteering is null) return;
        var removed = aiSteering.ClearPending();
        UpdateAiSteeringBanner();
        if (removed.Count > 0) AiStatus.Text = $"已撤回 {removed.Count} 条尚未注入的提示。";
    }

    private void UpdateAiSteeringBanner()
    {
        var pending = aiSteering?.PendingMessages;
        if (pending is not { Count: > 0 })
        {
            AiSteeringBanner.Visibility = Visibility.Collapsed;
            return;
        }
        AiSteeringCount.Text = pending.Count == 1
            ? "待注入 · 当前步骤完成后应用"
            : $"待注入 {pending.Count} 条 · 当前步骤完成后应用";
        AiSteeringText.Text = pending[^1];
        AiSteeringBanner.Visibility = Visibility.Visible;
    }

    private void DrainAiAppliedSteering()
    {
        while (aiAppliedSteering.TryDequeue(out var applied))
        {
            if (applied.Generation != aiProjectGeneration) continue;
            var row = AppendAiTranscript("你", applied.Message, heading: "调整方向");
            if (aiActivityRow is not null && AiTranscriptItems.Children.Contains(aiActivityRow))
            {
                AiTranscriptItems.Children.Remove(row);
                AiTranscriptItems.Children.Insert(AiTranscriptItems.Children.IndexOf(aiActivityRow), row);
            }
        }
        UpdateAiSteeringBanner();
    }

    private void RestoreAiComposerAfterInterrupted(string original, AiAgentSteeringQueue steering)
    {
        var pending = steering.DrainPending();
        var draft = AiPromptInput.Text.Trim();
        AiPromptInput.Text = string.Join("\n\n", new[] { original }
            .Concat(pending).Append(draft).Where(part => !string.IsNullOrWhiteSpace(part)));
        UpdateAiSteeringBanner();
    }

    private void RestoreAiPendingSteering(IReadOnlyList<string> pending)
    {
        var draft = AiPromptInput.Text.Trim();
        AiPromptInput.Text = string.Join("\n\n", pending.Append(draft)
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        AiStatus.Text = "本轮已结束；未注入的提示已放回输入栏。";
    }

    private static string AiCacheStatus(AiAgentUsageTotals? usage)
    {
        if (usage?.PromptCacheHitTokens is not { } hit ||
            usage.PromptCacheMissTokens is not { } miss) return "";
        var reportedTotal = (decimal)hit + miss;
        if (reportedTotal == 0) return "";
        return $" 本轮 API 已报告输入缓存命中 {hit:N0} / {reportedTotal:N0} tokens（{(double)((decimal)hit / reportedTotal):P0}）。";
    }

    private async Task<bool> RefreshAiWorkspaceAfterToolsAsync(string project, AiAgentTurn turn, int generation)
    {
        var changedWorkspace = turn.ProtocolMessages?.Any(message =>
            message.ToolCalls?.Any(AiToolMayChangeWorkspace) == true) == true;
        if (!changedWorkspace) return false;
        var needsReview = false;
        foreach (var session in editorDocuments.ToArray())
        {
            if (generation != aiProjectGeneration ||
                !string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase)) return needsReview;
            SourceDocument disk;
            try { disk = await services.Files.ReadAsync(project, session.Source.RelativePath); }
            catch (Exception ex)
            {
                Log("AI 工具操作后刷新编辑器失败：" + session.Source.RelativePath + "：" + ex.Message);
                needsReview = true;
                continue;
            }
            if (generation != aiProjectGeneration ||
                !string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase)) return needsReview;
            if (disk.DiskHash == session.Source.DiskHash) continue;
            if (session.IsDirty) { needsReview = true; continue; }
            // 文件由 MCP 修改后丢弃旧撤销基线，防止编辑器再次保存旧源码。
            if (session.Changed is not null) session.Buffer.TextChanged -= session.Changed;
            try
            {
                session.Source = disk;
                session.Buffer.Text = disk.Text;
                session.Buffer.UndoStack.ClearAll();
                UpdateEditorHeader(session);
            }
            finally { if (session.Changed is not null) session.Buffer.TextChanged += session.Changed; }
        }
        if (generation != aiProjectGeneration ||
            !string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase)) return needsReview;
        RefreshProjectTree();
        try { await RefreshExplorerLanguageAsync(CancellationToken.None); }
        catch (Exception ex) { Log("AI 工具操作后语言服务刷新失败：" + ex); }
        return needsReview;
    }

    private static bool AiToolMayChangeWorkspace(AiToolCall call)
    {
        if (call.Name is "project_edit_file" or "project_patch_file" or "project_create_file" or
            "project_create_directory" or "external_project_copy") return true;
        if (call.Name is not ("git_branch" or "git_remote")) return false;
        try
        {
            using var arguments = JsonDocument.Parse(call.ArgumentsJson);
            if (!arguments.RootElement.TryGetProperty("action", out var action) ||
                action.ValueKind != JsonValueKind.String) return false;
            return call.Name == "git_branch" ? action.GetString() is "switch" or "merge"
                : action.GetString() == "pull";
        }
        catch (JsonException) { return false; }
    }

    private Grid AppendAiTranscript(string role, string content, AiAgentTurn? turn = null,
        string? heading = null)
    {
        var isUser = role == "你";
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = isUser ? new GridLength(28) : new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = isUser ? new GridLength(1, GridUnitType.Star) : new GridLength(28) });
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 8, 11, 9)
        };
        bubble.SetResourceReference(Border.BackgroundProperty, isUser ? "Accent" : "ChromeSurface");
        bubble.SetResourceReference(Border.BorderBrushProperty, isUser ? "Accent" : "Border");
        var body = new StackPanel();
        var label = new TextBlock { Text = heading ?? role, FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        if (isUser) label.Foreground = Brushes.White;
        else label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var message = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
        if (isUser) message.Foreground = Brushes.White;
        else message.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        body.Children.Add(label);
        body.Children.Add(message);
        if (role == "AI" && turn is not null) AddAiTurnDetails(body, turn);
        bubble.Child = body;
        Grid.SetColumn(bubble, isUser ? 1 : 0);
        row.Children.Add(bubble);
        AiTranscriptItems.Children.Add(row);
        UpdateAiEmptyHint();
        AiTranscript.ScrollToEnd();
        return row;
    }

    private void UpdateAiEmptyHint()
    {
        var empty = AiTranscriptItems.Children.Count == 0;
        AiEmptyHint.Visibility = empty && AiTranscript.Visibility == Visibility.Visible &&
            AiPromptPanel.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        AiUnconfiguredHint.Visibility = empty && AiTranscript.Visibility == Visibility.Visible &&
            AiPromptPanel.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private string AiDraftKey(AiConversation? conversation) =>
        (projectDirectory ?? "") + "\n" + (conversation?.Id ?? "new");

    private void SetActiveAiConversation(AiConversation? conversation, bool projectChanged = false)
    {
        if (projectChanged)
        {
            aiDrafts.Clear();
            AiPromptInput.Clear();
        }
        else if (aiActiveConversation?.Id != conversation?.Id)
        {
            aiDrafts[AiDraftKey(aiActiveConversation)] = AiPromptInput.Text;
            AiPromptInput.Text = aiDrafts.TryGetValue(AiDraftKey(conversation), out var draft) ? draft : "";
        }
        aiActiveConversation = conversation;
        AiConversationHeading.Text = conversation?.Title ?? "";
        AiConversationHeading.Visibility = conversation is null ? Visibility.Collapsed : Visibility.Visible;
        aiHistory = conversation?.Turns ?? [];
        aiHistorySaveFailed = false;
        AiTranscriptItems.Children.Clear();
        if (conversation is not null)
        {
            foreach (var turn in conversation.Turns)
            {
                AppendAiTranscript("你", turn.User);
                foreach (var steering in turn.SteeringMessages ?? [])
                    AppendAiTranscript("你", steering, heading: "调整方向");
                AppendAiTranscript("AI", turn.Assistant, turn);
            }
        }
        UpdateAiEmptyHint();
        if (aiDisplaySettings is { } settings)
            UpdateAiContextMeter(settings, conversation?.LastPromptTokens,
                conversation is { Turns.Count: > 0 },
                conversation?.LastPromptCacheHitTokens,
                conversation?.LastPromptCacheMissTokens);
        UpdateAiConversationButtons();
    }

    private void RefreshAiConversationPicker()
    {
        aiConversationPickerUpdating = true;
        AiConversationPicker.ItemsSource = null;
        AiConversationPicker.ItemsSource = aiConversations;
        AiConversationPicker.SelectedItem = aiConversations.FirstOrDefault(item => item.Id == aiActiveConversation?.Id);
        aiConversationPickerUpdating = false;
        AiHistoryCount.Text = $"{aiConversations.Count} 条";
        AiHistoryEmptyHint.Visibility = aiConversations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAiConversationButtons();
    }

    private void ReplaceAiConversation(AiConversation conversation)
    {
        aiConversations = [conversation, .. aiConversations.Where(item => item.Id != conversation.Id)];
        RefreshAiConversationPicker();
    }

    private void UpdateAiConversationButtons()
    {
        var available = projectDirectory is not null && !aiConversationLoading &&
            !aiConversationOperationBusy && aiCancellation is null;
        AiHistoryButton.IsEnabled = available;
        AiConversationPicker.IsEnabled = available && aiConversations.Count > 0;
        AiNewConversationButton.IsEnabled = available;
        AiDeleteConversationButton.IsEnabled = available && aiActiveConversation is not null;
        AiModelButton.IsEnabled = AiPromptPanel.Visibility == Visibility.Visible &&
            !aiConversationOperationBusy && aiCancellation is null;
        AiModelButtonText.MaxWidth = aiCancellation is null ? 165 : 115;
        AiSendButton.IsEnabled = AiPromptPanel.Visibility == Visibility.Visible &&
            (aiCancellation is not null
                ? aiSteering?.IsAccepting == true
                : available && !aiHistorySaveFailed);
        var adjusting = aiCancellation is not null;
        AiSendButton.ToolTip = adjusting ? "调整方向：下次模型请求前注入提示" : "发送";
        System.Windows.Automation.AutomationProperties.SetName(AiSendButton,
            adjusting ? "运行中调整方向" : "发送 AI 消息");
        AiPromptInput.ToolTip = adjusting
            ? "运行中输入提示，点击发送后会在下次模型请求前应用"
            : "向 AI 助手提问；工程修改会逐次请求授权";
    }

    private async Task LoadAiConversationsForProjectAsync(string project, int generation)
    {
        try
        {
            var conversations = await services.AiConversations.ListAsync(project);
            if (generation != aiProjectGeneration) return;
            aiConversations = conversations;
            SetActiveAiConversation(conversations.FirstOrDefault());
            RefreshAiConversationPicker();
            AiStatus.Text = conversations.Count == 0
                ? "当前工程尚无 AI 对话，可直接提问或新建对话。"
                : $"已恢复当前工程的 {conversations.Count} 条对话。模型仅接收最近的对话轮次。";
        }
        catch (Exception ex)
        {
            if (generation == aiProjectGeneration)
                AiStatus.Text = "读取工程 AI 历史失败：" + ex.Message;
        }
        finally
        {
            if (generation == aiProjectGeneration)
            {
                aiConversationLoading = false;
                UpdateAiConversationButtons();
            }
        }
    }

    private void AiConversationPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (aiConversationPickerUpdating || aiCancellation is not null || aiConversationOperationBusy ||
            AiConversationPicker.SelectedItem is not AiConversation conversation ||
            conversation.Id == aiActiveConversation?.Id) return;
        AiHistoryPopup.IsOpen = false;
        SetActiveAiConversation(conversation);
        AiStatus.Text = $"已切换到“{conversation.Title}”。";
    }

    private async void AiNewConversation_Click(object sender, RoutedEventArgs e)
    {
        if (projectDirectory is not { } project || aiCancellation is not null ||
            aiConversationLoading || aiConversationOperationBusy) return;
        CloseAiPopups();
        if (aiActiveConversation is { Turns.Count: 0 })
        {
            AiStatus.Text = "已经是新对话，可以直接提问。";
            AiPromptInput.Focus();
            return;
        }
        var generation = aiProjectGeneration;
        aiConversationOperationBusy = true;
        UpdateAiConversationButtons();
        try
        {
            var conversation = await services.AiConversations.CreateAsync(project);
            if (generation != aiProjectGeneration ||
                !string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase)) return;
            aiConversations = [conversation, .. aiConversations];
            SetActiveAiConversation(conversation);
            RefreshAiConversationPicker();
            AiStatus.Text = "已新建对话。原有历史仍可从列表打开。";
            AiPromptInput.Focus();
        }
        catch (Exception ex)
        {
            if (generation == aiProjectGeneration) AiStatus.Text = "新建对话失败：" + ex.Message;
        }
        finally
        {
            aiConversationOperationBusy = false;
            UpdateAiConversationButtons();
        }
    }

    private async void AiDeleteConversation_Click(object sender, RoutedEventArgs e)
    {
        if (projectDirectory is not { } project || aiActiveConversation is not { } selected ||
            aiCancellation is not null || aiConversationLoading || aiConversationOperationBusy) return;
        if (MessageBox.Show(this, $"删除当前工程中的“{selected.Title}”？此操作无法撤销。",
                "删除 AI 对话", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var generation = aiProjectGeneration;
        aiConversationOperationBusy = true;
        UpdateAiConversationButtons();
        try
        {
            await services.AiConversations.DeleteAsync(project, selected.Id);
            if (generation != aiProjectGeneration ||
                !string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase) ||
                aiActiveConversation?.Id != selected.Id) return;
            aiConversations = aiConversations.Where(item => item.Id != selected.Id).ToArray();
            SetActiveAiConversation(aiConversations.FirstOrDefault());
            aiDrafts.Remove(AiDraftKey(selected));
            RefreshAiConversationPicker();
            AiHistoryPopup.IsOpen = false;
            AiStatus.Text = "已删除所选对话。";
        }
        catch (Exception ex)
        {
            if (generation == aiProjectGeneration) AiStatus.Text = "删除对话失败：" + ex.Message;
        }
        finally
        {
            aiConversationOperationBusy = false;
            UpdateAiConversationButtons();
        }
    }

    private void AiThinkingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateAiThinkingLabel();
        if (aiThinkingSliderUpdating || !IsLoaded || aiDisplaySettings is null ||
            !AiSettingsService.SupportsReasoningEffort(aiDisplaySettings)) return;
        var effort = ((int)Math.Round(AiThinkingSlider.Value)) switch
        {
            0 => "none", 1 => "low", 3 => "max", _ => "high"
        };
        var previous = aiThinkingSaveTask;
        var generation = ++aiThinkingSaveGeneration;
        aiThinkingSaveTask = SaveAiThinkingEffortAsync(previous, effort, generation);
    }

    private async Task SaveAiThinkingEffortAsync(Task previous, string effort, int generation)
    {
        try
        {
            await previous;
            if (generation != aiThinkingSaveGeneration) return;
            var settings = await services.AiSettings.LoadAsync();
            if (!AiSettingsService.SupportsReasoningEffort(settings)) return;
            await services.AiSettings.SaveAsync(settings with { ReasoningEffort = effort });
            aiDisplaySettings = settings with { ReasoningEffort = effort };
        }
        catch (Exception ex)
        {
            AiStatus.Text = "保存思考强度失败：" + ex.Message;
            await RefreshAiConfigurationAsync();
        }
    }

    private void UpdateAiThinkingLabel()
    {
        if (AiThinkingSlider is null || AiThinkingValue is null) return;
        var intensity = ((int)Math.Round(AiThinkingSlider.Value)) switch
        {
            0 => "关闭", 1 => "低", 3 => "最大", _ => "高"
        };
        AiThinkingValue.Text = intensity;
        if (AiModelButtonText is null) return;
        var model = aiDisplaySettings?.Model ?? "模型";
        var supported = aiDisplaySettings is { } settings && AiSettingsService.SupportsReasoningEffort(settings);
        AiModelButtonText.Text = supported ? $"{model} · {intensity}" : model;
        AiModelButton.ToolTip = supported ? $"{model} · {intensity}；点击调整思考强度" : model;
    }

    private void UpdateAiContextMeter(AiSettings settings, int? promptTokens, bool hasRequest,
        long? cacheHitTokens = null, long? cacheMissTokens = null)
    {
        var window = AiSettingsService.EffectiveContextWindowTokens(settings);
        AiContextArc.Visibility = AiContextFull.Visibility = Visibility.Collapsed;
        var description = "尚无上下文用量。";
        if (promptTokens is { } used && window is { } maximum)
        {
            var fraction = Math.Clamp((double)used / maximum, 0, 1);
            description = $"上下文已用 {fraction:P0}\n最近一次请求输入 {used:N0} / {maximum:N0} tokens";
            if (fraction >= 1)
                AiContextFull.Visibility = Visibility.Visible;
            else if (fraction > 0)
            {
                const double center = 11;
                const double radius = 8.5;
                var angle = fraction * 2 * Math.PI - Math.PI / 2;
                var end = new Point(center + radius * Math.Cos(angle), center + radius * Math.Sin(angle));
                var figure = new PathFigure
                {
                    StartPoint = new Point(center, center - radius),
                    IsClosed = false,
                    IsFilled = false
                };
                figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0,
                    fraction > 0.5, SweepDirection.Clockwise, true));
                var geometry = new PathGeometry();
                geometry.Figures.Add(figure);
                AiContextArc.Data = geometry;
                AiContextArc.Visibility = Visibility.Visible;
            }
        }
        else if (promptTokens is { } count)
            description = $"最近一次请求输入 {count:N0} tokens；上下文窗口容量未知。";
        else if (hasRequest)
            description = "API 未提供 token 用量。";
        string cacheText;
        string cacheDetails;
        if (cacheHitTokens is { } hit && cacheMissTokens is { } miss)
        {
            var reportedTotal = (decimal)hit + miss;
            if (reportedTotal > 0)
            {
                var rate = (double)((decimal)hit / reportedTotal);
                cacheText = $"最近一轮缓存命中 {hit:N0} / {reportedTotal:N0} tokens · {rate:P0}";
                cacheDetails = $"最近一轮 API 已报告：命中 {hit:N0} tokens，未命中 {miss:N0} tokens，命中率 {rate:P1}。";
            }
            else
            {
                cacheText = "最近一轮缓存：API 返回 0 / 0 tokens";
                cacheDetails = "最近一轮 API 返回的缓存命中与未命中 token 均为 0，无法计算命中率。";
            }
        }
        else if (cacheHitTokens is { } reportedHit)
        {
            cacheText = $"最近一轮缓存命中 {reportedHit:N0} tokens · 未返回未命中量";
            cacheDetails = "API 只返回缓存命中 token，未返回未命中 token；无法计算命中率。";
        }
        else if (cacheMissTokens is { } reportedMiss)
        {
            cacheText = $"最近一轮缓存未命中 {reportedMiss:N0} tokens · 未返回命中量";
            cacheDetails = "API 只返回缓存未命中 token，未返回命中 token；无法计算命中率。";
        }
        else if (hasRequest)
        {
            cacheText = "缓存：这段对话暂无 API 用量记录";
            cacheDetails = "旧对话未保存缓存字段，或 API 未返回缓存用量。下次成功请求后会更新。";
        }
        else
        {
            cacheText = "缓存：发送后显示 API 用量";
            cacheDetails = "发送消息后显示 API 实际返回的缓存命中和未命中 token。";
        }
        AiCacheUsageText.Text = cacheText;
        AiCacheUsageText.ToolTip = cacheDetails;
        System.Windows.Automation.AutomationProperties.SetName(AiCacheUsageText, cacheDetails);
        description += "\n" + cacheDetails;
        AiContextBadge.ToolTip = description;
        System.Windows.Automation.AutomationProperties.SetName(AiContextBadge, description);
    }

    private static string AiConversationTitle(string prompt)
    {
        var title = new string(prompt.Where(character => !char.IsControl(character)).Take(40).ToArray()).Trim();
        return title.Length == 0 ? "新对话" : title;
    }

    private void ResetAiForProjectChange()
    {
        CloseAiPopups();
        aiCancellation?.Cancel();
        aiSteering = null;
        UpdateAiSteeringBanner();
        FinishAiActivity();
        QueueAiMcpSessionDisposal();
        var generation = ++aiProjectGeneration;
        aiConversationLoading = projectDirectory is not null;
        aiConversations = [];
        SetActiveAiConversation(null, projectChanged: true);
        AiCancelButton.IsEnabled = false;
        AiCancelButton.Visibility = Visibility.Collapsed;
        RefreshAiConversationPicker();
        AiStatus.Text = projectDirectory is null
            ? "打开工程后可以提问。工程文本会发送到所配置的 API 服务。"
            : "正在读取当前工程的 AI 对话历史…";
        if (projectDirectory is { } project)
            _ = LoadAiConversationsForProjectAsync(project, generation);
    }
}
