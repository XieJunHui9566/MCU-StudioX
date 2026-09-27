

namespace StudioX.Application;

/// <summary>请求与响应共用的工具名称规则，保证重放时使用相同的协议约束。</summary>
internal static class AiChatProtocolRules
{
    internal static bool ValidToolName(string? name) => name is { Length: >= 1 and <= 128 } &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}
