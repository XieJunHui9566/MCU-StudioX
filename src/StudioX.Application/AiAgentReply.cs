namespace StudioX.Application;

public sealed record AiAgentReply(string Text, IReadOnlyList<AiAgentTurn> History,
    AiTokenUsage? Usage = null, string? ReasoningContent = null,
    AiAgentUsageTotals? AggregateUsage = null);
