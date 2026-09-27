using System.Text.Json;
using StudioX.Foundation;
using static StudioX.Application.AiChatProtocolLimits;
using static StudioX.Application.AiChatProtocolRules;

namespace StudioX.Application;

/// <summary>解析完整响应与 API 用量；缺失字段保留未知，错误字段拒绝进入会话历史。</summary>
internal static class AiChatResponseParser
{
    internal static AiChatResponse Parse(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            throw new StudioXException("AI_RESPONSE_FORMAT", "AI API 未返回对话结果。");
        }
        var choice = choices[0];
        if (choice.ValueKind != JsonValueKind.Object ||
            !choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
        {
            throw new StudioXException("AI_RESPONSE_FORMAT", "AI API 响应缺少消息。");
        }
        var content = OptionalString(message, "content");
        var reasoningContent = OptionalString(message, "reasoning_content");
        if (reasoningContent is { Length: > MaximumReasoningChars })
        {
            throw new StudioXException("AI_RESPONSE_SIZE", "AI API 推理内容超过会话上限。");
        }
        var finishReason = OptionalString(choice, "finish_reason");
        var model = OptionalString(root, "model");
        var usage = ParseUsage(root);
        var calls = new List<AiToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind != JsonValueKind.Null)
        {
            if (toolCalls.ValueKind != JsonValueKind.Array || toolCalls.GetArrayLength() > 16)
            {
                throw new StudioXException("AI_RESPONSE_FORMAT", "AI API 工具调用数量无效。");
            }
            foreach (var call in toolCalls.EnumerateArray())
            {
                if (call.ValueKind != JsonValueKind.Object || OptionalString(call, "type") != "function" ||
                    !call.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object)
                {
                    throw new StudioXException("AI_RESPONSE_FORMAT", "AI API 工具调用格式无效。");
                }
                var id = OptionalString(call, "id");
                var name = OptionalString(function, "name");
                var arguments = OptionalString(function, "arguments");
                if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || !ValidToolName(name) ||
                    arguments is null or { Length: > 512 * 1024 })
                {
                    throw new StudioXException("AI_RESPONSE_FORMAT", "AI API 工具调用内容无效。");
                }
                calls.Add(new AiToolCall(id, name!, arguments));
            }
        }
        if (content is null && calls.Count == 0)
        {
            throw new StudioXException("AI_RESPONSE_FORMAT", "AI API 返回了空消息。");
        }
        return new AiChatResponse(content, calls, finishReason, model, usage, reasoningContent);
    }

    internal static AiTokenUsage? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (usage.ValueKind != JsonValueKind.Object)
        {
            throw new StudioXException("AI_RESPONSE_FORMAT", "AI API 用量字段格式无效。");
        }
        var prompt = OptionalTokenCount(usage, "prompt_tokens");
        var completion = OptionalTokenCount(usage, "completion_tokens");
        var total = OptionalTokenCount(usage, "total_tokens");
        var cacheHit = OptionalTokenCount(usage, "prompt_cache_hit_tokens");
        var cacheMiss = OptionalTokenCount(usage, "prompt_cache_miss_tokens");
        return prompt is null && completion is null && total is null && cacheHit is null && cacheMiss is null
            ? null : new AiTokenUsage(prompt, completion, total, cacheHit, cacheMiss);
    }

    private static int? OptionalTokenCount(JsonElement usage, string property)
    {
        if (!usage.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var count) || count < 0)
        {
            throw new StudioXException("AI_RESPONSE_FORMAT", "AI API 用量字段格式无效。");
        }
        return count;
    }

    internal static string? OptionalString(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var field) ||
            field.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (field.ValueKind != JsonValueKind.String)
        {
            throw new StudioXException("AI_RESPONSE_FORMAT", "AI API 响应字段类型无效。");
        }
        return field.GetString();
    }
}
