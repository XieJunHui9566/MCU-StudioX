namespace StudioX.Application.Mcp;

/// <summary>工具图像只在当前模型请求中使用，不能写进对话历史或工具文本。</summary>
public sealed record StudioXMcpImage(string MimeType, byte[] Data);
