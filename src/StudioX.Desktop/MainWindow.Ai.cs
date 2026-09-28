namespace StudioX.Desktop;

using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Foundation;

public partial class MainWindow
{
    private CancellationTokenSource? aiCancellation;
    private AiAgentSteeringQueue? aiSteering;
    private readonly ConcurrentQueue<(int Generation, string Message)> aiAppliedSteering = new();
    private AiSettings? aiDisplaySettings;
    private Task aiThinkingSaveTask = Task.CompletedTask;
    private int aiThinkingSaveGeneration;
    private bool aiConversationPickerUpdating;
    private bool aiThinkingSliderUpdating;
    private bool aiConversationLoading;
    private bool aiConversationOperationBusy;
    private bool aiCollapsedOutline;
    private double savedAiSidebarWidth = 360;

    private AiConversationController? aiConversationController;
    private AiMcpSessionCoordinator? aiMcpCoordinator;

    private AiConversationController AiConversations => aiConversationController ??= new(services.AiConversations);
    private int aiProjectGeneration => AiConversations.Generation;

    private AiMcpSessionCoordinator AiMcpCoordinator => aiMcpCoordinator ??= new(
        IsCurrentAiEditorProject, CreateAiMcpSessionAsync, Log);

    private Task<StudioXMcpSession> GetAiMcpSessionAsync(string project, int generation, CancellationToken token) =>
        AiMcpCoordinator.GetAsync(project, generation, token);

    private async Task<StudioXMcpSession> CreateAiMcpSessionAsync(
        string project, int generation, CancellationToken token)
    {
        var authorizer = new DesktopMcpAuthorizer(this, candidate =>
            !closing && !closed && string.Equals(projectDirectory, candidate, StringComparison.OrdinalIgnoreCase),
            (request, approvalToken) => RequestAiMcpApprovalAsync(request, generation, approvalToken));
        var tools = new StudioXMcpTools(services, project, authorizer, AiHasUnsavedDocumentsAsync);
        try
        {
            return await StudioXMcpSession.CreateAsync(tools, token);
        }
        catch
        {
            await tools.DisposeAsync();
            throw;
        }
    }

    private Task<bool> AiHasUnsavedDocumentsAsync() => Dispatcher.CheckAccess()
        ? Task.FromResult(HasUnsavedAiProjectChanges())
        : Dispatcher.InvokeAsync(HasUnsavedAiProjectChanges).Task;

    private bool HasUnsavedAiProjectChanges() => editorDocuments.Any(session => session.IsDirty) ||
        (ag32PinPlanProject == projectDirectory && Ag32PinMapping.Planner.HasChanges);

    private void QueueAiMcpSessionDisposal() => aiMcpCoordinator?.QueueDisposal();

    private async Task DisposeAiMcpSessionAsync()
    {
        aiCancellation?.Cancel();
        if (aiMcpCoordinator is not null)
        {
            await aiMcpCoordinator.DisposeAsync();
        }
    }

    private async void AiAssistant_Click(object sender, RoutedEventArgs e)
    {
        if (AiSidebar.Visibility == Visibility.Visible)
        {
            HideAiSidebar();
            return;
        }
        await ShowAiSidebarAsync();
    }

    private async void AiSettingsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (aiCancellation is not null || aiConversationOperationBusy)
        {
            Status.Text = "请先停止当前 AI 请求。";
            return;
        }
        CloseAiPopups();
        try
        {
            await aiThinkingSaveTask;
            var settings = await services.AiSettings.LoadAsync();
            var dialog = new AiSettingsWindow(services.AiSettings, services.AiCredentials, services.WebCredentials, settings,
                services.EspressifDocumentation) { Owner = this };
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
        if (AiHistoryPopup.IsOpen)
        {
            AiHistoryPopup.Child?.Focus();
        }
    }

    private void AiModelButton_Click(object sender, RoutedEventArgs e)
    {
        AiHistoryPopup.IsOpen = false;
        AiModelPopup.IsOpen = !AiModelPopup.IsOpen;
        if (AiModelPopup.IsOpen)
        {
            AiModelPopup.Child?.Focus();
        }
    }

    private void AiPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }
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
        if (AiPromptPanel.Visibility == Visibility.Visible)
        {
            AiPromptInput.Focus();
        }
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
                "none" => 0,
                "low" => 1,
                "max" => 3,
                _ => 2
            };
            aiThinkingSliderUpdating = false;
            UpdateAiThinkingLabel();
            var reasoningSupported = AiSettingsService.SupportsReasoningEffort(settings);
            AiThinkingSlider.IsEnabled = configured && reasoningSupported && aiCancellation is null;
            AiThinkingHint.Text = reasoningSupported ? "关闭     ·     低     ·     高     ·     最大" : "当前接口未声明思考强度支持";
            UpdateAiContextMeter(settings, AiConversations.Active?.LastPromptTokens,
                AiConversations.Active is { Turns.Count: > 0 },
                AiConversations.Active?.LastPromptCacheHitTokens,
                AiConversations.Active?.LastPromptCacheMissTokens);
            if (!configured)
            {
                AiStatus.Text = "请先保存 API Key；当前工程的历史对话仍可查看。";
            }
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
        if (AiSidebar.Visibility != Visibility.Visible)
        {
            return;
        }
        CloseAiPopups();
        savedAiSidebarWidth = Math.Clamp(AiSidebarColumn.ActualWidth, 320, 620);
        AiSidebar.Visibility = AiSidebarSplitter.Visibility = Visibility.Collapsed;
        AiSidebarColumn.Width = AiSidebarSplitterColumn.Width = new GridLength(0);
        AiToggleButton.ToolTip = "打开右侧 AI 助手";
        System.Windows.Automation.AutomationProperties.SetName(AiToggleButton, "打开右侧 AI 助手");
        if (aiCollapsedOutline && OutlinePanel.Visibility == Visibility.Collapsed)
        {
            SetOutlineVisible(true);
        }
        aiCollapsedOutline = false;
    }

    private async void AiSend_Click(object sender, RoutedEventArgs e)
    {
        if (aiCancellation is not null)
        {
            QueueAiSteeringFromComposer();
            return;
        }
        if (aiConversationLoading || aiConversationOperationBusy || AiConversations.SaveFailed)
        {
            return;
        }
        if (projectDirectory is not { } project)
        {
            AiStatus.Text = "请先创建或打开工程。";
            return;
        }
        if (editorDocuments.Any(session => session.IsDirty))
        {
            AiStatus.Text = "编辑器中有未保存的文件。请先保存，使 AI 读取到当前代码。";
            return;
        }
        var prompt = AiPromptInput.Text.Trim();
        if (prompt.Length == 0)
        {
            AiStatus.Text = "请输入问题或任务。";
            return;
        }
        if (prompt.Length > 4_000)
        {
            AiStatus.Text = "单次提问不能超过 4,000 字符。";
            return;
        }
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
                !string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (!services.AiCredentials.HasApiKey(settings.BaseUrl))
            {
                await RefreshAiConfigurationAsync();
                return;
            }
            conversation = AiConversations.Active ?? await AiConversations.CreateAsync(project, generation);
            if (generation != aiProjectGeneration ||
                !string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (AiConversations.Active is null)
            {
                SetActiveAiConversation(conversation);
                AiConversations.ForgetNewConversationDraft();
                RefreshAiConversationPicker();
            }
        }
        catch (Exception ex)
        {
            if (generation == aiProjectGeneration)
            {
                AiStatus.Text = "无法开始 AI 请求：" + ex.Message;
            }
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
            var sendTask = agent.SendAsync(project, prompt, AiConversations.History, cancellation.Token, AiActivity.Progress,
                steering);
            AiMcpCoordinator.TrackCall(sendTask);
            var reply = await sendTask;
            if (generation != aiProjectGeneration ||
                !string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            DrainAiActivityProgress();
            DrainAiAppliedSteering();
            SetAiActivityStatus("正在刷新工程状态并保存对话…");
            var turn = reply.History.LastOrDefault() ?? throw new InvalidOperationException("AI 没有返回本轮对话记录。");
            await aiEditorSyncTask;
            if (generation != aiProjectGeneration ||
                !string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            var workspaceNeedsReview = await RefreshAiWorkspaceAfterToolsAsync(project, turn, generation);
            if (generation != aiProjectGeneration ||
                !string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            var saved = await AiConversations.SaveReplyAsync(project, generation, conversation, prompt, turn, reply);
            if (generation != aiProjectGeneration)
            {
                return;
            }
            var updated = saved.Conversation;
            var saveError = saved.Error;
            if (saveError is not null)
            {
                Log("AI 对话历史保存失败：" + saveError);
            }
            RefreshAiConversationPicker();
            AiConversationHeading.Text = updated.Title;
            FinishAiActivity();
            AppendAiTranscript("AI", reply.Text, turn);
            UpdateAiContextMeter(settings, reply.Usage?.PromptTokens, hasRequest: true,
                reply.AggregateUsage?.PromptCacheHitTokens,
                reply.AggregateUsage?.PromptCacheMissTokens);
            AiStatus.Text = saveError is not null
                ? "回答已显示，但对话历史保存失败：" + saveError.Message + " 请复制所需内容并新建对话。"
                : "已完成。" + AiCacheStatus(reply.AggregateUsage);
            if (workspaceNeedsReview)
            {
                AiStatus.Text += " 编辑器中有后续未保存的修改，已保留；请核对磁盘文件。";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (generation == aiProjectGeneration)
            {
                DrainAiActivityProgress();
                DrainAiAppliedSteering();
                await aiEditorSyncTask;
                if (AiActivity.HasReceivedContent)
                {
                    RetainInterruptedAiActivity("请求已停止；上方保留本轮已收到的内容与工具记录，此轮未存入历史。");
                }
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
            Log("AI 请求失败：" + ex);
            if (generation == aiProjectGeneration)
            {
                DrainAiActivityProgress();
                DrainAiAppliedSteering();
                if (ex is StudioXException { Code: "AI_RESPONSE_FORMAT" } &&
                    ex.Message.StartsWith("模型把工具调用写成了普通文本", StringComparison.Ordinal))
                {
                    ClearAiAnswerPreview();
                }
                await aiEditorSyncTask;
                if (AiActivity.HasReceivedContent)
                {
                    RetainInterruptedAiActivity("请求失败；上方保留本轮已收到的内容与工具记录，此轮未存入历史。");
                }
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
            {
                RestoreAiPendingSteering(unconsumed);
            }
            if (ReferenceEquals(aiSteering, steering))
            {
                aiSteering = null;
            }
            if (ReferenceEquals(aiCancellation, cancellation))
            {
                aiCancellation = null;
            }
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
        if (text.Length == 0)
        {
            AiStatus.Text = "请输入要调整的方向。";
            return;
        }
        if (text.Length > 4_000)
        {
            AiStatus.Text = "单次提示不能超过 4,000 字符。";
            return;
        }
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
        if (aiSteering is null)
        {
            return;
        }
        var removed = aiSteering.ClearPending();
        UpdateAiSteeringBanner();
        if (removed.Count > 0)
        {
            AiStatus.Text = $"已撤回 {removed.Count} 条尚未注入的提示。";
        }
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
            if (applied.Generation != aiProjectGeneration)
            {
                continue;
            }
            var row = AppendAiTranscript("你", applied.Message, heading: "调整方向");
            if (AiActivity.Row is not null && AiTranscriptItems.Children.Contains(AiActivity.Row))
            {
                AiTranscriptItems.Children.Remove(row);
                AiTranscriptItems.Children.Insert(AiTranscriptItems.Children.IndexOf(AiActivity.Row), row);
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
            usage.PromptCacheMissTokens is not { } miss)
        {
            return "";
        }
        var reportedTotal = (decimal)hit + miss;
        if (reportedTotal == 0)
        {
            return "";
        }
        return $" 本轮 API 已报告输入缓存命中 {hit:N0} / {reportedTotal:N0} tokens（{(double)((decimal)hit / reportedTotal):P0}）。";
    }

    private Grid AppendAiTranscript(string role, string content, AiAgentTurn? turn = null,
        string? heading = null)
    {
        var row = AiTranscriptRenderer.CreateBubble(role, content, turn, heading);
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

    private void SetActiveAiConversation(AiConversation? conversation, bool projectChanged = false)
    {
        AiPromptInput.Text = AiConversations.Select(conversation, AiPromptInput.Text, projectChanged);
        AiConversationHeading.Text = conversation?.Title ?? "";
        AiConversationHeading.Visibility = conversation is null ? Visibility.Collapsed : Visibility.Visible;
        AiTranscriptItems.Children.Clear();
        if (conversation is not null)
        {
            foreach (var turn in conversation.Turns)
            {
                AppendAiTranscript("你", turn.User);
                foreach (var steering in turn.SteeringMessages ?? [])
                {
                    AppendAiTranscript("你", steering, heading: "调整方向");
                }
                AppendAiTranscript("AI", turn.Assistant, turn);
            }
        }
        UpdateAiEmptyHint();
        if (aiDisplaySettings is { } settings)
        {
            UpdateAiContextMeter(settings, conversation?.LastPromptTokens,
            conversation is { Turns.Count: > 0 },
            conversation?.LastPromptCacheHitTokens,
            conversation?.LastPromptCacheMissTokens);
        }
        UpdateAiConversationButtons();
    }

    private void RefreshAiConversationPicker()
    {
        aiConversationPickerUpdating = true;
        AiConversationPicker.ItemsSource = null;
        AiConversationPicker.ItemsSource = AiConversations.Conversations;
        AiConversationPicker.SelectedItem = AiConversations.Conversations.FirstOrDefault(item => item.Id == AiConversations.Active?.Id);
        aiConversationPickerUpdating = false;
        AiHistoryCount.Text = $"{AiConversations.Conversations.Count} 条";
        AiHistoryEmptyHint.Visibility = AiConversations.Conversations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAiConversationButtons();
    }

    private void UpdateAiConversationButtons()
    {
        RefreshPluginCommandState();
        var available = projectDirectory is not null && !aiConversationLoading &&
            !aiConversationOperationBusy && aiCancellation is null;
        AiHistoryButton.IsEnabled = available;
        AiConversationPicker.IsEnabled = available && AiConversations.Conversations.Count > 0;
        AiNewConversationButton.IsEnabled = available;
        AiDeleteConversationButton.IsEnabled = available && AiConversations.Active is not null;
        AiModelButton.IsEnabled = AiPromptPanel.Visibility == Visibility.Visible &&
            !aiConversationOperationBusy && aiCancellation is null;
        AiModelButtonText.MaxWidth = aiCancellation is null ? 165 : 115;
        AiSendButton.IsEnabled = AiPromptPanel.Visibility == Visibility.Visible &&
            (aiCancellation is not null
                ? aiSteering?.IsAccepting == true
                : available && !AiConversations.SaveFailed);
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
            if (!await AiConversations.LoadAsync(project, generation))
            {
                return;
            }
            var conversations = AiConversations.Conversations;
            SetActiveAiConversation(conversations.FirstOrDefault());
            RefreshAiConversationPicker();
            AiStatus.Text = conversations.Count == 0
                ? "当前工程尚无 AI 对话，可直接提问或新建对话。"
                : $"已恢复当前工程的 {conversations.Count} 条对话。模型仅接收最近的对话轮次。";
        }
        catch (Exception ex)
        {
            if (generation == aiProjectGeneration)
            {
                AiStatus.Text = "读取工程 AI 历史失败：" + ex.Message;
            }
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
            conversation.Id == AiConversations.Active?.Id)
        {
            return;
        }
        AiHistoryPopup.IsOpen = false;
        SetActiveAiConversation(conversation);
        AiStatus.Text = $"已切换到“{conversation.Title}”。";
    }

    private async void AiNewConversation_Click(object sender, RoutedEventArgs e)
    {
        if (projectDirectory is not { } project || aiCancellation is not null ||
            aiConversationLoading || aiConversationOperationBusy)
        {
            return;
        }
        CloseAiPopups();
        if (AiConversations.Active is { Turns.Count: 0 })
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
            var conversation = await AiConversations.CreateAsync(project, generation);
            if (generation != aiProjectGeneration ||
                !string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            SetActiveAiConversation(conversation);
            RefreshAiConversationPicker();
            AiStatus.Text = "已新建对话。原有历史仍可从列表打开。";
            AiPromptInput.Focus();
        }
        catch (Exception ex)
        {
            if (generation == aiProjectGeneration)
            {
                AiStatus.Text = "新建对话失败：" + ex.Message;
            }
        }
        finally
        {
            aiConversationOperationBusy = false;
            UpdateAiConversationButtons();
        }
    }

    private async void AiDeleteConversation_Click(object sender, RoutedEventArgs e)
    {
        if (projectDirectory is not { } project || AiConversations.Active is not { } selected ||
            aiCancellation is not null || aiConversationLoading || aiConversationOperationBusy)
        {
            return;
        }
        if (MessageBox.Show(this, $"删除当前工程中的“{selected.Title}”？此操作无法撤销。",
                "删除 AI 对话", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        var generation = aiProjectGeneration;
        aiConversationOperationBusy = true;
        UpdateAiConversationButtons();
        try
        {
            await AiConversations.DeleteAsync(project, generation, selected);
            if (generation != aiProjectGeneration ||
                !string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase) ||
                AiConversations.Active?.Id != selected.Id)
            {
                return;
            }
            SetActiveAiConversation(AiConversations.Conversations.FirstOrDefault());
            RefreshAiConversationPicker();
            AiHistoryPopup.IsOpen = false;
            AiStatus.Text = "已删除所选对话。";
        }
        catch (Exception ex)
        {
            if (generation == aiProjectGeneration)
            {
                AiStatus.Text = "删除对话失败：" + ex.Message;
            }
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
            !AiSettingsService.SupportsReasoningEffort(aiDisplaySettings))
        {
            return;
        }
        var effort = ((int)Math.Round(AiThinkingSlider.Value)) switch
        {
            0 => "none",
            1 => "low",
            3 => "max",
            _ => "high"
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
            if (generation != aiThinkingSaveGeneration)
            {
                return;
            }
            var settings = await services.AiSettings.LoadAsync();
            if (!AiSettingsService.SupportsReasoningEffort(settings))
            {
                return;
            }
            await services.AiSettings.SaveAsync(settings with
            {
                ReasoningEffort = effort
            });
            aiDisplaySettings = settings with
            {
                ReasoningEffort = effort
            };
        }
        catch (Exception ex)
        {
            AiStatus.Text = "保存思考强度失败：" + ex.Message;
            await RefreshAiConfigurationAsync();
        }
    }

    private void UpdateAiThinkingLabel()
    {
        if (AiThinkingSlider is null || AiThinkingValue is null)
        {
            return;
        }
        var intensity = ((int)Math.Round(AiThinkingSlider.Value)) switch
        {
            0 => "关闭",
            1 => "低",
            3 => "最大",
            _ => "高"
        };
        AiThinkingValue.Text = intensity;
        if (AiModelButtonText is null)
        {
            return;
        }
        var model = aiDisplaySettings?.Model ?? "模型";
        var supported = aiDisplaySettings is { } settings && AiSettingsService.SupportsReasoningEffort(settings);
        AiModelButtonText.Text = supported ? $"{model} · {intensity}" : model;
        AiModelButton.ToolTip = supported ? $"{model} · {intensity}；点击调整思考强度" : model;
    }

    private AiUsageMeterPresenter? aiUsageMeterPresenter;

    private void UpdateAiContextMeter(AiSettings settings, int? promptTokens, bool hasRequest,
        long? cacheHitTokens = null, long? cacheMissTokens = null)
    {
        aiUsageMeterPresenter ??= new(AiContextArc, AiContextFull, AiContextBadge, AiCacheUsageText);
        aiUsageMeterPresenter.Update(settings, promptTokens, hasRequest, cacheHitTokens, cacheMissTokens);
    }

    private void ResetAiForProjectChange()
    {
        CloseAiPopups();
        aiCancellation?.Cancel();
        aiSteering = null;
        UpdateAiSteeringBanner();
        FinishAiActivity();
        QueueAiMcpSessionDisposal();
        var generation = AiConversations.BindProject(projectDirectory);
        aiConversationLoading = projectDirectory is not null;
        SetActiveAiConversation(null, projectChanged: true);
        AiCancelButton.IsEnabled = false;
        AiCancelButton.Visibility = Visibility.Collapsed;
        RefreshAiConversationPicker();
        AiStatus.Text = projectDirectory is null
            ? "打开工程后可以提问。工程文本会发送到所配置的 API 服务。"
            : "正在读取当前工程的 AI 对话历史…";
        if (projectDirectory is { } project)
        {
            _ = LoadAiConversationsForProjectAsync(project, generation);
        }
    }
}
