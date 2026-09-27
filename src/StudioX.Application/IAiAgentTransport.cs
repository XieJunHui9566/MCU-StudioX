namespace StudioX.Application;

/// <summary>可替换的模型调用入口，供离线验证注入伪响应。</summary>
public interface IAiAgentTransport
{
    Task<AiChatResponse> CompleteAsync(AiSettings settings, AiChatRequest request, CancellationToken token = default);
}
