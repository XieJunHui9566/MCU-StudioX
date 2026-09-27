namespace StudioX.Application;

/// <summary>可选的流式传输；离线伪传输仍可只实现非流式入口。</summary>
public interface IAiStreamingTransport : IAiAgentTransport
{
    Task<AiChatResponse> CompleteStreamingAsync(AiSettings settings, AiChatRequest request,
        Action<AiStreamUpdate> onUpdate, CancellationToken token = default);
}
