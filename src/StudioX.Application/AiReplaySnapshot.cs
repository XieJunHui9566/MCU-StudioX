namespace StudioX.Application;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Foundation;

/// <summary>对话最新的完整协议快照；无损压缩仅用于本地存储，不改变发给模型的消息。</summary>
public sealed record AiReplaySnapshot(int FormatVersion, string Identity, byte[] MessagesGzip)
{
    private const int MaximumDecodedBytes = 8 * 1024 * 1024;
    private const int MaximumCompressedBytes = 384 * 1024;

    internal static string Scope(string project, string system, IReadOnlyList<AiToolDefinition> tools) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            project = OperatingSystem.IsWindows() ? project.ToUpperInvariant() : project,
            system,
            tools
        })));

    internal static AiReplaySnapshot? Create(string identity, IReadOnlyList<AiChatMessage> messages)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(messages);
        if (bytes.Length > MaximumDecodedBytes)
        {
            return null;
        }
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(bytes);
        }
        return output.Length <= MaximumCompressedBytes ? new(1, identity, output.ToArray()) : null;
    }

    internal IReadOnlyList<AiChatMessage>? Read(string identity)
    {
        if (Identity != identity)
        {
            return null;
        }
        if (FormatVersion != 1 || MessagesGzip is null or { Length: 0 or > MaximumCompressedBytes })
        {
            throw new StudioXException("AI_HISTORY_FORMAT", "AI 协议快照格式或大小无效。");
        }
        try
        {
            using var input = new MemoryStream(MessagesGzip, writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int count;
            while ((count = gzip.Read(buffer)) > 0)
            {
                if (output.Length + count > MaximumDecodedBytes)
                {
                    throw new StudioXException("AI_HISTORY_SIZE", "AI 协议快照解压超过上限。");
                }
                output.Write(buffer, 0, count);
            }
            return JsonSerializer.Deserialize<AiChatMessage[]>(output.ToArray())
                ?? throw new StudioXException("AI_HISTORY_FORMAT", "AI 协议快照为空。");
        }
        catch (Exception error) when (error is InvalidDataException or JsonException)
        {
            throw new StudioXException("AI_HISTORY_FORMAT", "AI 协议快照损坏。", error);
        }
    }
}
