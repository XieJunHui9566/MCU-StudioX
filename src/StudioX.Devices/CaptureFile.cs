namespace StudioX.Devices;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using StudioX.Foundation;

public sealed record CaptureHeader(int FormatVersion, string ClockDomain, long TicksPerSecond, string ConnectionKey, bool Simulated);
public sealed record CapturedFrame(long Sequence, long Timestamp, string Base64);
public static class CaptureFile
{
    public static async Task WriteAsync(string path, DeviceSession session, FrameSubscription subscription, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream);
        await writer.WriteLineAsync(JsonSerializer.Serialize(new CaptureHeader(1, "host-monotonic", Stopwatch.Frequency, session.Key, session.IsSimulated)));
        try
        {
            await foreach (var frame in subscription.Reader.ReadAllAsync(cancellationToken))
                await writer.WriteLineAsync(JsonSerializer.Serialize(new CapturedFrame(frame.Sequence, frame.Timestamp, Convert.ToBase64String(frame.Payload.Span))));
        }
        finally { await writer.FlushAsync(CancellationToken.None); }
    }
    public static async IAsyncEnumerable<CapturedFrame> ReadAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = File.OpenText(path);
        var header = JsonSerializer.Deserialize<CaptureHeader>(await reader.ReadLineAsync(cancellationToken) ?? "null");
        if (header is null || header.FormatVersion != 1 || header.ClockDomain != "host-monotonic" || header.TicksPerSecond <= 0)
            throw new StudioXException("CAPTURE_FORMAT", "记录文件格式不支持。");
        long previous = -1;
        long previousTime = -1;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var frame = JsonSerializer.Deserialize<CapturedFrame>(line) ?? throw new StudioXException("CAPTURE_FRAME", "记录帧为空。");
            if (frame.Sequence <= previous || frame.Timestamp < previousTime) throw new StudioXException("CAPTURE_ORDER", "记录帧顺序无效。");
            _ = Convert.FromBase64String(frame.Base64);
            previous = frame.Sequence; previousTime = frame.Timestamp;
            yield return frame;
        }
    }
}
