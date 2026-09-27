

namespace StudioX.Application;

/// <summary>将兼容端点的完整 JSON 响应转为通知，保持聊天界面的反馈方式一致。</summary>
internal static class AiStreamUpdateReporter
{
    internal static void Report(AiChatResponse response, Action<AiStreamUpdate> onUpdate)
    {
        if (!string.IsNullOrEmpty(response.ReasoningContent))
        {
            onUpdate(new AiStreamUpdate(AiStreamUpdateKind.ReasoningDelta, response.ReasoningContent));
        }
        if (!string.IsNullOrEmpty(response.Content))
        {
            onUpdate(new AiStreamUpdate(AiStreamUpdateKind.ContentDelta, response.Content));
        }
        for (var index = 0; index < response.ToolCalls.Count; index++)
        {
            onUpdate(new AiStreamUpdate(AiStreamUpdateKind.ToolCall,
            ToolName: response.ToolCalls[index].Name, ToolCallIndex: index));
        }
        if (response.Usage is not null)
        {
            onUpdate(new AiStreamUpdate(AiStreamUpdateKind.Usage, Usage: response.Usage));
        }
    }
}
