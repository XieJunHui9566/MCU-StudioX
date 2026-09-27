using System.Text;
using StudioX.Foundation;
using static StudioX.Application.AiChatProtocolLimits;

namespace StudioX.Application;

/// <summary>解码 UTF-8 与 SSE 事件边界；只有收到 [DONE] 才接受完整响应。</summary>
internal static class AiSseResponseReader
{
    internal static async Task<AiChatResponse> ReadAsync(HttpContent content,
        Action<AiStreamUpdate> onUpdate, CancellationToken token)
    {
        if (content.Headers.ContentLength is > MaximumStreamBytes)
        {
            throw new StudioXException("AI_RESPONSE_SIZE", "AI API 流式响应过大。");
        }

        await using var source = await content.ReadAsStreamAsync(token);
        using var line = new MemoryStream();
        var block = new byte[16 * 1024];
        var accumulator = new AiStreamAccumulator(onUpdate);
        var parser = new EventBuffer(accumulator.AppendEvent);
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(block, token)) > 0)
        {
            total += read;
            if (total > MaximumStreamBytes)
            {
                throw new StudioXException("AI_RESPONSE_SIZE", "AI API 流式响应过大。");
            }
            for (var index = 0; index < read; index++)
            {
                if (block[index] == (byte)'\n')
                {
                    parser.AddLine(DecodeSseLine(line));
                    line.SetLength(0);
                    if (parser.Done)
                    {
                        return accumulator.BuildResponse();
                    }
                }
                else
                {
                    if (line.Length >= MaximumStreamLineBytes)
                    {
                        throw new StudioXException("AI_RESPONSE_SIZE", "AI API 流式事件过大。");
                    }
                    line.WriteByte(block[index]);
                }
            }
        }
        if (line.Length > 0)
        {
            parser.AddLine(DecodeSseLine(line));
        }
        parser.FlushEvent();
        if (!parser.Done)
        {
            throw new StudioXException("AI_RESPONSE_FORMAT", "AI API 流式响应未正常结束。");
        }
        return accumulator.BuildResponse();
    }

    private static string DecodeSseLine(MemoryStream line)
    {
        var bytes = line.GetBuffer().AsSpan(0, checked((int)line.Length));
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
        {
            bytes = bytes[..^1];
        }
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new StudioXException("AI_RESPONSE_FORMAT", "AI API 流式响应不是有效的 UTF-8 文本。");
        }
    }

    // data: 可跨多行；空行才结束一个事件，不能按网络读取块直接解析 JSON。
    private sealed class EventBuffer(Action<string> onEvent)
    {
        private readonly StringBuilder eventData = new();

        public bool Done
        {
            get; private set;
        }

        public void AddLine(string line)
        {
            if (Done)
            {
                return;
            }
            line = line.TrimStart('\uFEFF');
            if (line.Length == 0)
            {
                FlushEvent();
                return;
            }
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                return;
            }
            var value = line.AsSpan(5);
            if (value.Length > 0 && value[0] == ' ')
            {
                value = value[1..];
            }
            if (eventData.Length + value.Length + 1 > MaximumStreamLineBytes)
            {
                throw new StudioXException("AI_RESPONSE_SIZE", "AI API 流式事件过大。");
            }
            if (eventData.Length > 0)
            {
                eventData.Append('\n');
            }
            eventData.Append(value);
        }

        public void FlushEvent()
        {
            if (Done || eventData.Length == 0)
            {
                return;
            }
            var payload = eventData.ToString();
            eventData.Clear();
            if (payload == "[DONE]")
            {
                Done = true;
                return;
            }

            onEvent(payload);
        }
    }
}
