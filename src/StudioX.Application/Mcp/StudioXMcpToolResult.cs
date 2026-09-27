namespace StudioX.Application.Mcp;

/// <summary>一次 MCP 调用的文本及独立图像块；错误文本由会话层转换为可修复诊断。</summary>
public sealed record StudioXMcpToolResult(string Text, IReadOnlyList<StudioXMcpImage> Images);
