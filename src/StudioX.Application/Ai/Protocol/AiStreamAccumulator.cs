using System.Text;
using System.Text.Json;
using StudioX.Foundation;
using static StudioX.Application.AiChatProtocolLimits;
using static StudioX.Application.AiChatResponseParser;

namespace StudioX.Application;

/// <summary>按工具序号归并增量，保留推理、回答与服务端实际 token 用量。</summary>
internal sealed class AiStreamAccumulator(Action<AiStreamUpdate> onUpdate)
{
    private readonly StringBuilder reasoning = new();
    private readonly StringBuilder content = new();
    private readonly SortedDictionary<int, AiStreamToolCall> calls = [];
    private bool seenContent;
    private string? model;
    private string? finishReason;
    private AiTokenUsage? usage;

    public void AppendEvent(string payload)
    {
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            throw new StudioXException("AI_RESPONSE_FORMAT", "AI API 流式事件格式无效。");
        }
        model = OptionalString(root, "model") ?? model;
        if (ParseUsage(root) is { } eventUsage)
        {
            usage = eventUsage;
            onUpdate(new AiStreamUpdate(AiStreamUpdateKind.Usage, Usage: eventUsage));
        }
        if (choices.GetArrayLength() == 0)
        {
            return;
        }
        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object ||
            !choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
        {
            throw new StudioXException("AI_RESPONSE_FORMAT", "AI API 流式增量格式无效。");
        }
        finishReason = OptionalString(choice, "finish_reason") ?? finishReason;
        if (OptionalString(delta, "reasoning_content") is { Length: > 0 } thought)
        {
            if (reasoning.Length + thought.Length > MaximumReasoningChars)
            {
                throw new StudioXException("AI_RESPONSE_SIZE", "AI API 推理内容超过会话上限。");
            }
            reasoning.Append(thought);
            onUpdate(new AiStreamUpdate(AiStreamUpdateKind.ReasoningDelta, thought));
        }
        if (OptionalString(delta, "content") is { } answer)
        {
            seenContent = true;
            if (content.Length + answer.Length > MaximumResponseBytes)
            {
                throw new StudioXException("AI_RESPONSE_SIZE", "AI API 回答超过会话上限。");
            }
            content.Append(answer);
            if (answer.Length > 0)
            {
                onUpdate(new AiStreamUpdate(AiStreamUpdateKind.ContentDelta, answer));
            }
        }
        if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind != JsonValueKind.Null)
        {
            if (toolCalls.ValueKind != JsonValueKind.Array)
            {
                throw new StudioXException("AI_RESPONSE_FORMAT", "AI API 工具调用格式无效。");
            }
            foreach (var call in toolCalls.EnumerateArray())
            {
                AppendToolCall(call);
            }
        }
    }

    private void AppendToolCall(JsonElement call)
    {
        if (call.ValueKind != JsonValueKind.Object ||
            !call.TryGetProperty("index", out var indexElement) ||
            indexElement.ValueKind != JsonValueKind.Number ||
            !indexElement.TryGetInt32(out var index) || index is < 0 or >= 16)
        {
            throw new StudioXException("AI_RESPONSE_FORMAT", "AI API 工具调用序号无效。");
        }
        if (!calls.TryGetValue(index, out var current))
        {
            current = new AiStreamToolCall();
            calls.Add(index, current);
        }
        if (OptionalString(call, "type") is { } type && type != "function")
        {
            throw new StudioXException("AI_RESPONSE_FORMAT", "AI API 工具调用类型无效。");
        }
        if (OptionalString(call, "id") is { } id)
        {
            current.AppendId(id);
        }
        if (!call.TryGetProperty("function", out var function) || function.ValueKind == JsonValueKind.Null)
        {
            return;
        }
        if (function.ValueKind != JsonValueKind.Object)
        {
            throw new StudioXException("AI_RESPONSE_FORMAT", "AI API 工具调用函数无效。");
        }
        if (OptionalString(function, "name") is { Length: > 0 } name)
        {
            current.AppendName(name);
            onUpdate(new AiStreamUpdate(AiStreamUpdateKind.ToolCall,
                ToolName: current.Name, ToolCallIndex: index));
        }
        if (OptionalString(function, "arguments") is { } arguments)
        {
            current.AppendArguments(arguments);
        }
    }

    public AiChatResponse BuildResponse()
    {
        var toolCalls = calls.Select(entry => entry.Value.Build()).ToArray();
        if (!seenContent && toolCalls.Length == 0)
        {
            throw new StudioXException("AI_RESPONSE_FORMAT", "AI API 返回了空消息。");
        }
        return new AiChatResponse(seenContent ? content.ToString() : null, toolCalls,
            finishReason, model, usage, reasoning.Length > 0 ? reasoning.ToString() : null);
    }
}
