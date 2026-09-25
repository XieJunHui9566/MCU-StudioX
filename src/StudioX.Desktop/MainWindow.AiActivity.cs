namespace StudioX.Desktop;

using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StudioX.Application;

public partial class MainWindow
{
    private const int AiReasoningPreviewChars = 32_000;
    private const int AiAnswerPreviewChars = 16_000;
    private Grid? aiActivityRow;
    private TextBlock? aiActivityStatus;
    private TextBlock? aiActivityElapsed;
    private StackPanel? aiActivitySteps;
    private StackPanel? aiActivityAnswerPanel;
    private TextBlock? aiActivityAnswer;
    private ProgressBar? aiActivityPulse;
    private Expander? aiActivityReasoning;
    private TextBox? aiActivityReasoningText;
    private DispatcherTimer? aiActivityTimer;
    private DateTimeOffset aiActivityStarted;
    private readonly StringBuilder aiReceivedReasoning = new();
    private readonly StringBuilder aiReceivedAnswer = new();
    private long aiOmittedReasoningChars;
    private long aiOmittedAnswerChars;
    private bool aiReasoningPreviewDirty;
    private bool aiAnswerPreviewDirty;
    private AiUiProgressBuffer? aiProgressBuffer;
    private int aiReasoningRound;
    private int aiActivityToolCount;

    private void StartAiActivity()
    {
        FinishAiActivity();
        aiReceivedReasoning.Clear();
        aiReceivedAnswer.Clear();
        aiOmittedReasoningChars = 0;
        aiOmittedAnswerChars = 0;
        aiReasoningPreviewDirty = false;
        aiAnswerPreviewDirty = false;
        aiReasoningRound = 0;
        aiActivityToolCount = 0;
        aiActivityStarted = DateTimeOffset.UtcNow;
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 8, 11, 10)
        };
        bubble.SetResourceReference(Border.BackgroundProperty, "ChromeSurface");
        bubble.SetResourceReference(Border.BorderBrushProperty, "Accent");
        var body = new StackPanel();
        var heading = new DockPanel { LastChildFill = true };
        var elapsed = new TextBlock { FontSize = 10, Text = "0:00", VerticalAlignment = VerticalAlignment.Center };
        elapsed.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(elapsed, Dock.Right);
        heading.Children.Add(elapsed);
        var title = new TextBlock { Text = "AI 正在处理", FontSize = 11, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        heading.Children.Add(title);
        body.Children.Add(heading);
        var status = new TextBlock
        {
            Text = "正在准备工程工具…", TextWrapping = TextWrapping.Wrap,
            FontSize = 12, Margin = new Thickness(0, 8, 0, 0)
        };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        body.Children.Add(status);
        var pulse = new ProgressBar
        {
            IsIndeterminate = true, Height = 3, Margin = new Thickness(0, 9, 0, 0),
            IsHitTestVisible = false
        };
        body.Children.Add(pulse);
        var answerPanel = new StackPanel
        {
            Visibility = Visibility.Collapsed, Margin = new Thickness(0, 9, 0, 0)
        };
        var answerCaption = new TextBlock { Text = "模型输出预览（本轮未完成）", FontSize = 10 };
        answerCaption.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        answerPanel.Children.Add(answerCaption);
        var answer = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
            Margin = new Thickness(0, 4, 0, 0)
        };
        answer.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        answerPanel.Children.Add(answer);
        body.Children.Add(answerPanel);
        var steps = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        body.Children.Add(steps);
        var reasoningText = CreateAiDetailTextBox();
        var reasoning = new Expander
        {
            Header = "模型思考（已收到）", IsExpanded = false,
            Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0),
            ToolTip = "仅显示接口实际返回的 reasoning_content。"
        };
        reasoning.Content = reasoningText;
        body.Children.Add(reasoning);
        bubble.Child = body;
        row.Children.Add(bubble);
        AiTranscriptItems.Children.Add(row);
        aiActivityRow = row;
        aiActivityStatus = status;
        aiActivityElapsed = elapsed;
        aiActivitySteps = steps;
        aiActivityAnswerPanel = answerPanel;
        aiActivityAnswer = answer;
        aiActivityPulse = pulse;
        aiActivityReasoning = reasoning;
        aiActivityReasoningText = reasoningText;
        aiProgressBuffer = new AiUiProgressBuffer();
        aiActivityTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        aiActivityTimer.Tick += AiActivityTimer_Tick;
        aiActivityTimer.Start();
        UpdateAiEmptyHint();
        AiTranscript.ScrollToEnd();
    }

    private void AiActivityTimer_Tick(object? sender, EventArgs e)
    {
        DrainAiActivityProgress();
        if (aiActivityElapsed is not null)
        {
            var elapsed = DateTimeOffset.UtcNow - aiActivityStarted;
            var label = elapsed.TotalHours >= 1
                ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"m\:ss");
            if (aiActivityElapsed.Text != label) aiActivityElapsed.Text = label;
        }
        if (aiReasoningPreviewDirty && aiActivityReasoning is { IsExpanded: true } &&
            aiActivityReasoningText is { } text)
        {
            text.Text = RenderAiPreview(aiReceivedReasoning, AiReasoningPreviewChars,
                aiOmittedReasoningChars);
            aiReasoningPreviewDirty = false;
        }
        if (aiAnswerPreviewDirty && aiActivityAnswer is { } answer)
        {
            answer.Text = RenderAiPreview(aiReceivedAnswer, AiAnswerPreviewChars,
                aiOmittedAnswerChars);
            aiAnswerPreviewDirty = false;
            ScrollAiTranscriptIfNearEnd();
        }
    }

    private void SetAiActivityStatus(string status)
    {
        if (aiActivityStatus is null) return;
        aiActivityStatus.Text = status;
        ScrollAiTranscriptIfNearEnd();
    }

    private void AddAiActivityStep(string step)
    {
        if (aiActivitySteps is null) return;
        var line = new TextBlock
        {
            Text = "· " + step, TextWrapping = TextWrapping.Wrap,
            FontSize = 10.5, Margin = new Thickness(0, 2, 0, 0)
        };
        line.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        aiActivitySteps.Children.Add(line);
        while (aiActivitySteps.Children.Count > 6) aiActivitySteps.Children.RemoveAt(0);
        ScrollAiTranscriptIfNearEnd();
    }

    private void ScrollAiTranscriptIfNearEnd()
    {
        if (AiTranscript.ScrollableHeight - AiTranscript.VerticalOffset <= 36)
            AiTranscript.ScrollToEnd();
    }

    private void AppendAiReceivedReasoning(string content, int round)
    {
        if (string.IsNullOrEmpty(content) || aiActivityReasoning is null) return;
        if (round != aiReasoningRound)
        {
            if (aiReceivedReasoning.Length > 0)
                AppendAiPreview(aiReceivedReasoning, "\n\n", AiReasoningPreviewChars,
                    ref aiOmittedReasoningChars);
            aiReasoningRound = round;
        }
        AppendAiPreview(aiReceivedReasoning, content, AiReasoningPreviewChars,
            ref aiOmittedReasoningChars);
        aiReasoningPreviewDirty = true;
        if (aiActivityReasoning.Visibility != Visibility.Visible)
        {
            aiActivityReasoning.Visibility = Visibility.Visible;
            aiActivityReasoning.Header = "模型思考（已收到；点击展开）";
        }
    }

    private void AppendAiReceivedAnswer(string content)
    {
        if (string.IsNullOrEmpty(content) || aiActivityAnswerPanel is null) return;
        AppendAiPreview(aiReceivedAnswer, content, AiAnswerPreviewChars,
            ref aiOmittedAnswerChars);
        aiAnswerPreviewDirty = true;
        if (aiActivityAnswerPanel.Visibility != Visibility.Visible)
            aiActivityAnswerPanel.Visibility = Visibility.Visible;
    }

    private void ClearAiAnswerPreview()
    {
        aiReceivedAnswer.Clear();
        aiOmittedAnswerChars = 0;
        aiAnswerPreviewDirty = false;
        if (aiActivityAnswer is not null) aiActivityAnswer.Text = "";
        if (aiActivityAnswerPanel is not null)
            aiActivityAnswerPanel.Visibility = Visibility.Collapsed;
    }

    private static void AppendAiPreview(StringBuilder buffer, string content, int visibleChars,
        ref long omittedChars)
    {
        // 保留近期文本供展开查看；达到容量后成块回收，避免流式小段输出反复移动缓冲区。
        var capacity = visibleChars * 2;
        if (content.Length >= capacity)
        {
            omittedChars += (long)buffer.Length + content.Length - visibleChars;
            buffer.Clear();
            buffer.Append(content.AsSpan(content.Length - visibleChars));
            return;
        }
        buffer.Append(content);
        if (buffer.Length <= capacity) return;
        var discard = buffer.Length - visibleChars;
        buffer.Remove(0, discard);
        omittedChars += discard;
    }

    private static string RenderAiPreview(StringBuilder buffer, int visibleChars, long omittedChars)
    {
        var shown = Math.Min(visibleChars, buffer.Length);
        var hidden = omittedChars + buffer.Length - shown;
        var text = buffer.ToString(buffer.Length - shown, shown);
        return hidden > 0 ? $"仅显示最近内容；较早的 {hidden:N0} 字已从预览中省略。\n{text}" : text;
    }

    private void DrainAiActivityProgress()
    {
        if (aiProgressBuffer is null) return;
        string? status = null;
        foreach (var update in aiProgressBuffer.Drain())
        {
            switch (update.Kind)
            {
                case AiAgentProgressKind.ModelRequestStarted:
                    status = $"正在等待模型第 {update.Round} 轮响应…";
                    break;
                case AiAgentProgressKind.ReasoningDelta:
                    AppendAiReceivedReasoning(update.Text ?? "", update.Round);
                    status = $"正在接收模型第 {update.Round} 轮思考…";
                    break;
                case AiAgentProgressKind.AnswerDelta:
                    AppendAiReceivedAnswer(update.Text ?? "");
                    status = "正在接收模型回答…";
                    break;
                case AiAgentProgressKind.ModelResponseReceived:
                    status = $"模型第 {update.Round} 轮已返回，正在处理结果…";
                    break;
                case AiAgentProgressKind.ToolCallStarted:
                    aiActivityToolCount++;
                    AddAiActivityStep($"{update.ToolName ?? "工具"} · 正在调用");
                    status = $"正在调用 {update.ToolName ?? "工程工具"}…";
                    break;
                case AiAgentProgressKind.ToolCallCompleted:
                    CompleteAiActivityTool(update.ToolName ?? "工具");
                    status = $"{update.ToolName ?? "工具"} 已返回；等待模型继续…";
                    OnAiToolCompletedForEditor(update);
                    break;
            }
        }
        if (status is not null) SetAiActivityStatus(status);
    }

    private void CompleteAiActivityTool(string toolName)
    {
        if (aiActivitySteps is null) return;
        for (var index = aiActivitySteps.Children.Count - 1; index >= 0; index--)
        {
            if (aiActivitySteps.Children[index] is TextBlock line &&
                line.Text == $"· {toolName} · 正在调用")
            {
                line.Text = $"· {toolName} · 已返回";
                return;
            }
        }
        AddAiActivityStep($"{toolName} · 已返回");
    }

    private sealed class AiUiProgressBuffer : IProgress<AiAgentProgress>
    {
        private readonly object gate = new();
        private readonly Queue<AiAgentProgress> pending = new();
        private bool closed;

        public void Report(AiAgentProgress value)
        {
            lock (gate) if (!closed) pending.Enqueue(value);
        }

        public void Close()
        {
            lock (gate)
            {
                closed = true;
                pending.Clear();
            }
        }

        public AiAgentProgress[] Drain()
        {
            lock (gate)
            {
                if (pending.Count == 0) return [];
                var updates = pending.ToArray();
                pending.Clear();
                return updates;
            }
        }
    }

    private void FinishAiActivity()
    {
        DrainAiActivityProgress();
        if (aiActivityTimer is not null)
        {
            aiActivityTimer.Stop();
            aiActivityTimer.Tick -= AiActivityTimer_Tick;
            aiActivityTimer = null;
        }
        if (aiActivityRow is not null) AiTranscriptItems.Children.Remove(aiActivityRow);
        aiActivityRow = null;
        aiActivityStatus = null;
        aiActivityElapsed = null;
        aiActivitySteps = null;
        aiActivityAnswerPanel = null;
        aiActivityAnswer = null;
        aiActivityPulse = null;
        aiActivityReasoning = null;
        aiActivityReasoningText = null;
        aiProgressBuffer?.Close();
        aiProgressBuffer = null;
        aiReceivedReasoning.Clear();
        aiReceivedAnswer.Clear();
        aiOmittedReasoningChars = 0;
        aiOmittedAnswerChars = 0;
        aiReasoningPreviewDirty = false;
        aiAnswerPreviewDirty = false;
        UpdateAiEmptyHint();
    }

    private void RetainInterruptedAiActivity(string status)
    {
        DrainAiActivityProgress();
        if (aiActivityTimer is not null)
        {
            aiActivityTimer.Stop();
            aiActivityTimer.Tick -= AiActivityTimer_Tick;
        }
        if (aiActivityPulse is not null) aiActivityPulse.Visibility = Visibility.Collapsed;
        SetAiActivityStatus(status);
        if (aiActivityReasoningText is not null)
            aiActivityReasoningText.Text = RenderAiPreview(aiReceivedReasoning,
                AiReasoningPreviewChars, aiOmittedReasoningChars);
        if (aiActivityAnswer is not null)
            aiActivityAnswer.Text = RenderAiPreview(aiReceivedAnswer,
                AiAnswerPreviewChars, aiOmittedAnswerChars);
        aiActivityRow = null;
        aiActivityStatus = null;
        aiActivityElapsed = null;
        aiActivitySteps = null;
        aiActivityAnswerPanel = null;
        aiActivityAnswer = null;
        aiActivityPulse = null;
        aiActivityReasoning = null;
        aiActivityReasoningText = null;
        aiActivityTimer = null;
        aiProgressBuffer?.Close();
        aiProgressBuffer = null;
        aiReceivedReasoning.Clear();
        aiReceivedAnswer.Clear();
        aiOmittedReasoningChars = 0;
        aiOmittedAnswerChars = 0;
        aiReasoningPreviewDirty = false;
        aiAnswerPreviewDirty = false;
    }

    private static TextBox CreateAiDetailTextBox()
    {
        var text = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 240, Padding = new Thickness(3, 5, 3, 5),
            FontSize = 11, Background = Brushes.Transparent, BorderThickness = new Thickness(0)
        };
        text.SetResourceReference(Control.ForegroundProperty, "Text");
        return text;
    }

    private static void AddAiTurnDetails(StackPanel body, AiAgentTurn turn)
    {
        var protocol = turn.ProtocolMessages;
        var thoughts = protocol is { Count: > 0 }
            ? protocol.Where(message => message.Role == "assistant" &&
                !string.IsNullOrWhiteSpace(message.ReasoningContent))
                .Select(message => message.ReasoningContent!).ToArray()
            : [];
        if (thoughts.Length == 0 && !string.IsNullOrWhiteSpace(turn.ReasoningContent))
            thoughts = [turn.ReasoningContent];
        if (thoughts.Length > 0)
        {
            // 模型在工具轮次返回的思考分段保存于协议历史；仅在展开时加载大段文本。
            var reasoning = new Expander
            {
                Header = turn.CompactedToolCalls > 0
                    ? $"模型思考 · 保留 {thoughts.Length} 段（早期详情已回收）"
                    : thoughts.Length == 1 ? "模型思考（接口返回）" : $"模型思考 · {thoughts.Length} 段（接口返回）",
                Margin = new Thickness(0, 9, 0, 0),
                ToolTip = "显示当前保留的接口 reasoning_content。"
            };
            var text = CreateAiDetailTextBox();
            reasoning.Content = text;
            reasoning.Expanded += (_, _) => text.Text = string.Join("\n\n", thoughts);
            reasoning.Collapsed += (_, _) => text.Clear();
            body.Children.Add(reasoning);
        }
        else
        {
            var unavailable = new TextBlock
            {
                Text = "接口未返回可展示的思考内容", FontSize = 10,
                Margin = new Thickness(0, 8, 0, 0)
            };
            unavailable.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            body.Children.Add(unavailable);
        }

        var returned = (protocol ?? []).Where(message => message.Role == "tool" && message.ToolCallId is not null)
            .Select(message => message.ToolCallId!).ToHashSet(StringComparer.Ordinal);
        var calls = (protocol ?? []).Where(message => message.Role == "assistant" && message.ToolCalls is { Count: > 0 })
            .SelectMany(message => message.ToolCalls!).ToArray();
        var compacted = Math.Max(0, turn.CompactedToolCalls);
        if (calls.Length == 0 && compacted == 0) return;
        var totalCalls = compacted > long.MaxValue - calls.LongLength
            ? long.MaxValue : compacted + calls.LongLength;
        var process = new Expander
        {
            Header = $"工具过程 · {totalCalls} 次调用", Margin = new Thickness(0, 5, 0, 0),
            ToolTip = "显示保留的工具调用名称及是否收到返回；早期详情可能已回收。"
        };
        var lines = new StackPanel();
        if (compacted > 0)
        {
            var compactedLine = new TextBlock
            {
                Text = $"· 较早的 {compacted} 次调用详情已回收",
                TextWrapping = TextWrapping.Wrap, FontSize = 10.5, Margin = new Thickness(0, 3, 0, 0)
            };
            compactedLine.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            lines.Children.Add(compactedLine);
        }
        foreach (var call in calls)
        {
            var line = new TextBlock
            {
                Text = $"· {call.Name} · {(returned.Contains(call.Id) ? "已返回" : "未见返回记录")}",
                TextWrapping = TextWrapping.Wrap, FontSize = 10.5, Margin = new Thickness(0, 3, 0, 0)
            };
            line.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            lines.Children.Add(line);
        }
        process.Content = lines;
        body.Children.Add(process);
    }
}
