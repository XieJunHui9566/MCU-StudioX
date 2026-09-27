namespace StudioX.Application;

/// <summary>一轮 API 请求的数据；工具结果和临时图像由会话协调层提供。</summary>
public sealed record AiChatRequest(IReadOnlyList<AiChatMessage> Messages,
    IReadOnlyList<AiToolDefinition>? Tools = null, IReadOnlyList<AiRequestImage>? Images = null);
