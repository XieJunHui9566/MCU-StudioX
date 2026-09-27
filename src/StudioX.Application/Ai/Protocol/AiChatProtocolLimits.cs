

namespace StudioX.Application;

/// <summary>限制单次协议载荷的内存占用；这些边界不限制 Agent 的调用轮次。</summary>
internal static class AiChatProtocolLimits
{
    internal const int MaximumRequestBytes = 2 * 1024 * 1024;
    internal const int MaximumInlineImageBytes = 800 * 1024;
    internal const int MaximumInlineImageTotalBytes = 950 * 1024;
    internal const int MaximumResponseBytes = 4 * 1024 * 1024;
    internal const int MaximumStreamBytes = 8 * 1024 * 1024;
    internal const int MaximumStreamLineBytes = 1024 * 1024;
    internal const int MaximumReasoningChars = 512 * 1024;
}
