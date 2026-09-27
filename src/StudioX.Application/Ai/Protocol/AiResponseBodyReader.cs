using StudioX.Foundation;
using static StudioX.Application.AiChatProtocolLimits;

namespace StudioX.Application;

/// <summary>有界读取完整 JSON 响应，不依赖服务端是否声明 Content-Length。</summary>
internal static class AiResponseBodyReader
{
    internal static async Task<AiChatResponse> ReadAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new StudioXException("AI_RESPONSE_SIZE", "AI API 响应过大。");
        }

        var bytes = await ReadBytesAsync(content, token);
        try
        {
            return AiChatResponseParser.Parse(bytes);
        }
        finally
        {
            // 响应包含工程内容；解析完成后清除临时字节数组，历史由会话层另行保存。
            Array.Clear(bytes);
        }
    }

    private static async Task<byte[]> ReadBytesAsync(HttpContent content, CancellationToken token)
    {
        await using var source = await content.ReadAsStreamAsync(token);
        using var destination = new MemoryStream();
        var block = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(block, token)) > 0)
        {
            if (destination.Length + read > MaximumResponseBytes)
            {
                throw new StudioXException("AI_RESPONSE_SIZE", "AI API 响应过大。");
            }
            destination.Write(block, 0, read);
        }
        return destination.ToArray();
    }
}
