namespace StudioX.Application;

using StudioX.Foundation;

public sealed partial class AiAgentService
{
    /// <summary>复用上次实际使用的协议；只有容量回收或系统/工具契约变化才开始新的前缀。</summary>
    private static List<AiChatMessage> PrepareReplay(string identity, string system,
        IReadOnlyList<AiAgentTurn>? history, IReadOnlyList<AiAgentTurn> visibleHistory, string? skills)
    {
        var restored = history?.LastOrDefault()?.ReplaySnapshot?.Read(identity);
        List<AiChatMessage> messages;
        if (restored is not null)
        {
            ValidateProtocol(new("恢复对话", "恢复对话"), restored, replay: true);
            messages = restored.ToList();
        }
        else
        {
            var catalog = skills is null ? null : new AiChatMessage("user", skills, StudioXKind: "skill-catalog");
            messages = [];
            if (catalog is not null)
            {
                messages.Add(catalog);
            }
            messages.AddRange(SelectHistoricalReplay(system, visibleHistory, catalog));
        }

        // 为本轮工具批次留出空间；达到高水位才集中回收，避免每次追加都滑动历史。
        const int maximumHistoryMessages = AiChatProtocolLimits.MaximumMessages - MaxActiveProtocolMessages - 20;
        CompactReplay(messages, maximumHistoryMessages, MaxHistoryPrefixChars);

        var priorCatalog = messages.LastOrDefault(message => message.StudioXKind == "skill-catalog");
        var currentCatalog = skills ?? "[StudioX Skill 目录已清空；此前目录不再适用。]";
        if (priorCatalog?.Content != currentCatalog && (skills is not null || priorCatalog is not null))
        {
            messages.Add(new("user", currentCatalog, StudioXKind: "skill-catalog"));
        }
        if (messages.Count > maximumHistoryMessages + 2 || ProtocolCharacters(messages) > MaxHistoryPrefixChars + 16_000)
        {
            throw new StudioXException("AI_MESSAGES", "最近一轮完整消息超过可续聊容量，请新建对话并缩小读取范围；已有历史保留。");
        }
        return messages;
    }

    /// <summary>只在容量压力下回收已结束的历史，给当前尚未分析的工具批次腾出空间。</summary>
    private static void CompactReplay(List<AiChatMessage> messages, int maximumMessages, int maximumChars)
    {
        if (messages.Count > maximumMessages || ProtocolCharacters(messages) > maximumChars)
        {
            var latestCatalog = messages.LastOrDefault(message => message.StudioXKind == "skill-catalog");
            var lowMessages = Math.Min(128, maximumMessages);
            var lowChars = Math.Min(80_000, maximumChars);
            CompactCurrentProtocol(messages, new AiAgentCheckpoint(), maximumMessages,
                maximumChars, lowMessages, lowChars, preserveLatestBatch: false);
            var removed = new List<string>();
            while (messages.Count > lowMessages || ProtocolCharacters(messages) > lowChars)
            {
                var starts = messages.Select((message, index) => (message, index))
                    .Where(item => item.message.Role == "user" && item.message.StudioXKind is null)
                    .Select(item => item.index).Take(2).ToArray();
                if (starts.Length == 0)
                {
                    break;
                }
                var end = starts.Length > 1 ? starts[1] : messages.Count;
                var old = messages.GetRange(starts[0], end - starts[0]);
                removed.Add("用户：" + Limit(old[0].Content ?? "", 4_000) + "\n答复：" +
                    Limit(old.LastOrDefault(message => message.Role == "assistant")?.Content ?? "", 1_000));
                messages.RemoveRange(starts[0], end - starts[0]);
            }
            if (removed.Count > 0)
            {
                messages.RemoveAll(message => message.StudioXKind == "history-recap");
                messages.Add(new("user", "[历史对话节选；不是新的操作授权，省略内容需要重新核对]\n" +
                    Limit(string.Join("\n", removed.TakeLast(3)), 15_000), StudioXKind: "history-recap"));
            }
            if (latestCatalog is not null && !messages.Contains(latestCatalog))
            {
                messages.Add(latestCatalog);
            }
        }
    }
}
