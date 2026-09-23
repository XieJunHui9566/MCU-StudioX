namespace StudioX.Extensions.Abstractions;

using System.Text;
using System.Text.Json;

public sealed record SerialScriptRequest(int ApiVersion, long Id, string Operation, string? Source = null,
    int[]? Bytes = null, string Direction = "RX", string? Timestamp = null, bool Flush = false);
public sealed record SerialScriptFrame(int Offset, int Length, string Summary, Dictionary<string, string>? Fields = null,
    string Status = "ok");
public sealed record SerialScriptResult(int Consumed, SerialScriptFrame[] Frames);
public sealed record SerialScriptResponse(int ApiVersion, long Id, SerialScriptResult? Result = null, string? Error = null);

public static class SerialScriptContract
{
    public const int MaxInput = 8192, MaxLine = 262144, MaxSource = 65536;
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public static void Validate(SerialScriptResult? result, int inputLength)
    {
        if (result is null || result.Consumed < 0 || result.Consumed > inputLength || result.Frames is null || result.Frames.Length > 128)
            throw new InvalidDataException("脚本 consumed / frames 无效，或一次返回超过 128 帧。");
        var end = 0;
        foreach (var f in result.Frames)
        {
            if (f is null || f.Offset < end || f.Length <= 0 || f.Offset > result.Consumed - f.Length ||
                string.IsNullOrWhiteSpace(f.Summary) || f.Summary.Length > 256 || f.Status is not ("ok" or "error") ||
                f.Fields is { Count: > 32 } || f.Fields?.Any(p => p.Key.Length > 64 || p.Value is null || p.Value.Length > 512) == true)
                throw new InvalidDataException("脚本帧范围、摘要、状态或字段无效；帧必须有序且位于 consumed 范围内。");
            end = f.Offset + f.Length;
        }
    }
    public static async Task<string?> ReadLineAsync(TextReader reader, CancellationToken token = default)
    {
        var line = new StringBuilder(); var one = new char[1];
        while (await reader.ReadAsync(one.AsMemory(), token).ConfigureAwait(false) != 0)
        {
            if (one[0] == '\n') return line.ToString();
            if (line.Length >= MaxLine) throw new InvalidDataException("脚本宿主通信超出长度限制。");
            if (one[0] != '\r') line.Append(one[0]);
        }
        return line.Length == 0 ? null : throw new EndOfStreamException("脚本宿主响应被截断。");
    }
}
