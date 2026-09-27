namespace StudioX.Application;

/// <summary>只用于下一次模型请求的页面图像；不会加入可持久化的消息历史。</summary>
public sealed record AiRequestImage(string MimeType, byte[] Data, string Source);
