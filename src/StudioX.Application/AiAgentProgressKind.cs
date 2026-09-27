namespace StudioX.Application;

public enum AiAgentProgressKind
{
    ModelRequestStarted, ReasoningDelta, AnswerDelta, ModelResponseReceived,
    ToolCallStarted, ToolCallCompleted
}
