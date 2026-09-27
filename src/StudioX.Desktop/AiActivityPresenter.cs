namespace StudioX.Desktop;

using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StudioX.Application;

/// <summary>只负责当前请求的活动气泡、流式文本和计时，不拥有工程或 Agent 会话。</summary>
internal sealed class AiActivityPresenter(
    StackPanel transcriptItems,
    ScrollViewer transcript,
    Action updateEmptyHint,
    Action<AiAgentProgress> toolCompleted)
{
    public Grid? Row => row;
    public IProgress<AiAgentProgress>? Progress => progressBuffer;
    public bool HasReceivedContent => toolCount > 0 ||
        receivedReasoning.Length > 0 || receivedAnswer.Length > 0;

    private const int AiReasoningPreviewChars = 32_000;
    private const int AiAnswerPreviewChars = 16_000;
    private Grid? row;
    private TextBlock? statusText;
    private TextBlock? elapsedText;
    private StackPanel? stepsPanel;
    private StackPanel? answerPanel;
    private TextBlock? answerText;
    private ProgressBar? pulse;
    private Expander? reasoningPanel;
    private TextBox? reasoningText;
    private DispatcherTimer? timer;
    private DateTimeOffset startedUtc;
    private readonly StringBuilder receivedReasoning = new();
    private readonly StringBuilder receivedAnswer = new();
    private long omittedReasoningChars;
    private long omittedAnswerChars;
    private bool reasoningPreviewDirty;
    private bool answerPreviewDirty;
    private AiUiProgressBuffer? progressBuffer;
    private int reasoningRound;
    private int toolCount;

    public void ExpandReasoningPreview()
    {
        if (reasoningPanel is not null)
        {
            reasoningPanel.IsExpanded = true;
        }
        AiActivityTimer_Tick(this, EventArgs.Empty);
    }

    public void Start()
    {
        Finish();
        receivedReasoning.Clear();
        receivedAnswer.Clear();
        omittedReasoningChars = 0;
        omittedAnswerChars = 0;
        reasoningPreviewDirty = false;
        answerPreviewDirty = false;
        reasoningRound = 0;
        toolCount = 0;
        startedUtc = DateTimeOffset.UtcNow;
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
            Text = "正在准备工程工具…",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 0)
        };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        body.Children.Add(status);
        var pulse = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 3,
            Margin = new Thickness(0, 9, 0, 0),
            IsHitTestVisible = false
        };
        body.Children.Add(pulse);
        var answerPanel = new StackPanel
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 9, 0, 0)
        };
        var answerCaption = new TextBlock { Text = "模型输出预览（本轮未完成）", FontSize = 10 };
        answerCaption.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        answerPanel.Children.Add(answerCaption);
        var answer = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            Margin = new Thickness(0, 4, 0, 0)
        };
        answer.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        answerPanel.Children.Add(answer);
        body.Children.Add(answerPanel);
        var steps = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        body.Children.Add(steps);
        var reasoningText = AiTurnDetailRenderer.CreateTextBox();
        var reasoning = new Expander
        {
            Header = "模型思考（已收到）",
            IsExpanded = false,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0),
            ToolTip = "仅显示接口实际返回的 reasoning_content。"
        };
        reasoning.Content = reasoningText;
        body.Children.Add(reasoning);
        bubble.Child = body;
        row.Children.Add(bubble);
        transcriptItems.Children.Add(row);
        this.row = row;
        statusText = status;
        elapsedText = elapsed;
        stepsPanel = steps;
        this.answerPanel = answerPanel;
        answerText = answer;
        this.pulse = pulse;
        reasoningPanel = reasoning;
        this.reasoningText = reasoningText;
        progressBuffer = new AiUiProgressBuffer();
        timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        timer.Tick += AiActivityTimer_Tick;
        timer.Start();
        updateEmptyHint();
        transcript.ScrollToEnd();
    }

    private void AiActivityTimer_Tick(object? sender, EventArgs e)
    {
        DrainProgress();
        if (elapsedText is not null)
        {
            var elapsed = DateTimeOffset.UtcNow - startedUtc;
            var label = elapsed.TotalHours >= 1
                ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"m\:ss");
            if (elapsedText.Text != label)
            {
                elapsedText.Text = label;
            }
        }
        if (reasoningPreviewDirty && reasoningPanel is { IsExpanded: true } &&
            reasoningText is { } text)
        {
            text.Text = RenderAiPreview(receivedReasoning, AiReasoningPreviewChars,
                omittedReasoningChars);
            reasoningPreviewDirty = false;
        }
        if (answerPreviewDirty && answerText is { } answer)
        {
            answer.Text = RenderAiPreview(receivedAnswer, AiAnswerPreviewChars,
                omittedAnswerChars);
            answerPreviewDirty = false;
            ScrollIfNearEnd();
        }
    }

    public void SetStatus(string status)
    {
        if (statusText is null)
        {
            return;
        }
        statusText.Text = status;
        ScrollIfNearEnd();
    }

    private void AddAiActivityStep(string step)
    {
        if (stepsPanel is null)
        {
            return;
        }
        var line = new TextBlock
        {
            Text = "· " + step,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10.5,
            Margin = new Thickness(0, 2, 0, 0)
        };
        line.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        stepsPanel.Children.Add(line);
        while (stepsPanel.Children.Count > 6)
        {
            stepsPanel.Children.RemoveAt(0);
        }
        ScrollIfNearEnd();
    }

    private void ScrollIfNearEnd()
    {
        if (transcript.ScrollableHeight - transcript.VerticalOffset <= 36)
        {
            transcript.ScrollToEnd();
        }
    }

    private void AppendAiReceivedReasoning(string content, int round)
    {
        if (string.IsNullOrEmpty(content) || reasoningPanel is null)
        {
            return;
        }
        if (round != reasoningRound)
        {
            if (receivedReasoning.Length > 0)
            {
                AppendAiPreview(receivedReasoning, "\n\n", AiReasoningPreviewChars,
                ref omittedReasoningChars);
            }
            reasoningRound = round;
        }
        AppendAiPreview(receivedReasoning, content, AiReasoningPreviewChars,
            ref omittedReasoningChars);
        reasoningPreviewDirty = true;
        if (reasoningPanel.Visibility != Visibility.Visible)
        {
            reasoningPanel.Visibility = Visibility.Visible;
            reasoningPanel.Header = "模型思考（已收到；点击展开）";
        }
    }

    private void AppendAiReceivedAnswer(string content)
    {
        if (string.IsNullOrEmpty(content) || answerPanel is null)
        {
            return;
        }
        AppendAiPreview(receivedAnswer, content, AiAnswerPreviewChars,
            ref omittedAnswerChars);
        answerPreviewDirty = true;
        if (answerPanel.Visibility != Visibility.Visible)
        {
            answerPanel.Visibility = Visibility.Visible;
        }
    }

    public void ClearAnswerPreview()
    {
        receivedAnswer.Clear();
        omittedAnswerChars = 0;
        answerPreviewDirty = false;
        if (answerText is not null)
        {
            answerText.Text = "";
        }
        if (answerPanel is not null)
        {
            answerPanel.Visibility = Visibility.Collapsed;
        }
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
        if (buffer.Length <= capacity)
        {
            return;
        }
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

    public void DrainProgress()
    {
        if (progressBuffer is null)
        {
            return;
        }
        string? status = null;
        foreach (var update in progressBuffer.Drain())
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
                    toolCount++;
                    AddAiActivityStep($"{update.ToolName ?? "工具"} · 正在调用");
                    status = $"正在调用 {update.ToolName ?? "工程工具"}…";
                    break;
                case AiAgentProgressKind.ToolCallCompleted:
                    CompleteAiActivityTool(update.ToolName ?? "工具");
                    status = $"{update.ToolName ?? "工具"} 已返回；等待模型继续…";
                    toolCompleted(update);
                    break;
            }
        }
        if (status is not null)
        {
            SetStatus(status);
        }
    }

    private void CompleteAiActivityTool(string toolName)
    {
        if (stepsPanel is null)
        {
            return;
        }
        for (var index = stepsPanel.Children.Count - 1; index >= 0; index--)
        {
            if (stepsPanel.Children[index] is TextBlock line &&
                line.Text == $"· {toolName} · 正在调用")
            {
                line.Text = $"· {toolName} · 已返回";
                return;
            }
        }
        AddAiActivityStep($"{toolName} · 已返回");
    }

    public void Finish()
    {
        DrainProgress();
        if (timer is not null)
        {
            timer.Stop();
            timer.Tick -= AiActivityTimer_Tick;
            timer = null;
        }
        if (row is not null)
        {
            transcriptItems.Children.Remove(row);
        }
        row = null;
        statusText = null;
        elapsedText = null;
        stepsPanel = null;
        answerPanel = null;
        answerText = null;
        pulse = null;
        reasoningPanel = null;
        reasoningText = null;
        progressBuffer?.Close();
        progressBuffer = null;
        receivedReasoning.Clear();
        receivedAnswer.Clear();
        omittedReasoningChars = 0;
        omittedAnswerChars = 0;
        reasoningPreviewDirty = false;
        answerPreviewDirty = false;
        updateEmptyHint();
    }

    public void RetainInterrupted(string status)
    {
        DrainProgress();
        if (timer is not null)
        {
            timer.Stop();
            timer.Tick -= AiActivityTimer_Tick;
        }
        if (pulse is not null)
        {
            pulse.Visibility = Visibility.Collapsed;
        }
        SetStatus(status);
        if (reasoningText is not null)
        {
            reasoningText.Text = RenderAiPreview(receivedReasoning,
            AiReasoningPreviewChars, omittedReasoningChars);
        }
        if (answerText is not null)
        {
            answerText.Text = RenderAiPreview(receivedAnswer,
            AiAnswerPreviewChars, omittedAnswerChars);
        }
        row = null;
        statusText = null;
        elapsedText = null;
        stepsPanel = null;
        answerPanel = null;
        answerText = null;
        pulse = null;
        reasoningPanel = null;
        reasoningText = null;
        timer = null;
        progressBuffer?.Close();
        progressBuffer = null;
        receivedReasoning.Clear();
        receivedAnswer.Clear();
        omittedReasoningChars = 0;
        omittedAnswerChars = 0;
        reasoningPreviewDirty = false;
        answerPreviewDirty = false;
    }

}
