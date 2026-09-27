namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StudioX.Application;

/// <summary>按需展示已保存的模型协议详情；折叠后释放大段文本，保持历史列表轻量。</summary>
internal static class AiTurnDetailRenderer
{
    public static TextBox CreateTextBox()
    {
        var text = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 240,
            Padding = new Thickness(3, 5, 3, 5),
            FontSize = 11,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        text.SetResourceReference(Control.ForegroundProperty, "Text");
        return text;
    }

    public static void AddTurnDetails(StackPanel body, AiAgentTurn turn)
    {
        var protocol = turn.ProtocolMessages;
        var thoughts = protocol is { Count: > 0 }
            ? protocol.Where(message => message.Role == "assistant" &&
                !string.IsNullOrWhiteSpace(message.ReasoningContent))
                .Select(message => message.ReasoningContent!).ToArray()
            : [];
        if (thoughts.Length == 0 && !string.IsNullOrWhiteSpace(turn.ReasoningContent))
        {
            thoughts = [turn.ReasoningContent];
        }
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
            var text = CreateTextBox();
            reasoning.Content = text;
            reasoning.Expanded += (_, _) => text.Text = string.Join("\n\n", thoughts);
            reasoning.Collapsed += (_, _) => text.Clear();
            body.Children.Add(reasoning);
        }
        else
        {
            var unavailable = new TextBlock
            {
                Text = "接口未返回可展示的思考内容",
                FontSize = 10,
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
        if (calls.Length == 0 && compacted == 0)
        {
            return;
        }
        var totalCalls = compacted > long.MaxValue - calls.LongLength
            ? long.MaxValue : compacted + calls.LongLength;
        var process = new Expander
        {
            Header = $"工具过程 · {totalCalls} 次调用",
            Margin = new Thickness(0, 5, 0, 0),
            ToolTip = "显示保留的工具调用名称及是否收到返回；早期详情可能已回收。"
        };
        var lines = new StackPanel();
        if (compacted > 0)
        {
            var compactedLine = new TextBlock
            {
                Text = $"· 较早的 {compacted} 次调用详情已回收",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 10.5,
                Margin = new Thickness(0, 3, 0, 0)
            };
            compactedLine.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            lines.Children.Add(compactedLine);
        }
        foreach (var call in calls)
        {
            var line = new TextBlock
            {
                Text = $"· {call.Name} · {(returned.Contains(call.Id) ? "已返回" : "未见返回记录")}",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 10.5,
                Margin = new Thickness(0, 3, 0, 0)
            };
            line.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            lines.Children.Add(line);
        }
        process.Content = lines;
        body.Children.Add(process);
    }
}
