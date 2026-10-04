namespace StudioX.Application.MicroPython;

using System.Text;
using StudioX.Devices;
using StudioX.Foundation;

/// <summary>串行使用 raw REPL 帧；接收溢出、异常帧和超时不得被解释为执行成功。</summary>
internal sealed class RawReplClient(DeviceSession session, FrameSubscription frames)
{
    private byte[] current = [];
    private int position;

    public async Task EnterAsync(CancellationToken token)
    {
        await session.SendAsync(new byte[] { 13, 3, 3 }, token).ConfigureAwait(false);
        await Task.Delay(100, token).ConfigureAwait(false);
        await session.SendAsync(new byte[] { 1 }, token).ConfigureAwait(false);
        await ReadUntilAsync("raw REPL; CTRL-B to exit\r\n>"u8.ToArray(), token).ConfigureAwait(false);
    }

    public async Task<MicroPythonResult> ExecuteAsync(string code, CancellationToken token)
    {
        await StartAsync(code, token).ConfigureAwait(false);
        var output = await ReadUntilAsync([4], token).ConfigureAwait(false);
        var error = await ReadUntilAsync([4], token).ConfigureAwait(false);
        await ReadPromptAsync(token).ConfigureAwait(false);
        return new(Encoding.UTF8.GetString(output), Encoding.UTF8.GetString(error));
    }

    public async Task<bool> RunAsync(string code, Action<string> output, CancellationToken token)
    {
        // 启动握手仍有期限；主程序可以长期运行，不能沿用片段的 30 秒限制。
        using (var startup = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            startup.CancelAfter(TimeSpan.FromSeconds(10));
            await StartAsync(code, startup.Token).ConfigureAwait(false);
        }
        await StreamUntilEndAsync(output, token).ConfigureAwait(false);
        using var completion = CancellationTokenSource.CreateLinkedTokenSource(token);
        completion.CancelAfter(TimeSpan.FromSeconds(10));
        var hasError = await StreamUntilEndAsync(output, completion.Token).ConfigureAwait(false);
        await ReadPromptAsync(completion.Token).ConfigureAwait(false);
        return !hasError;
    }

    private async Task<bool> StreamUntilEndAsync(Action<string> output, CancellationToken token)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[1024];
        var chars = new char[1025];
        var count = 0;
        var received = false;
        while (true)
        {
            var value = await ReadByteAsync(token).ConfigureAwait(false);
            var ended = value == 4;
            if (!ended)
            {
                bytes[count++] = value;
                received = true;
            }
            // 串口暂时没有新帧前就交付输出；解码器保留跨帧的 UTF-8 字符。
            if (ended || count == bytes.Length || position >= current.Length)
            {
                var length = decoder.GetChars(bytes, 0, count, chars, 0, flush: ended);
                if (length != 0)
                {
                    output(new string(chars, 0, length));
                }
                count = 0;
            }
            if (ended)
            {
                return received;
            }
        }
    }

    private async Task StartAsync(string code, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(code);
        if (bytes.Length > 16384 || code.Any(value => value < ' ' && value is not ('\r' or '\n' or '\t')))
        {
            throw new StudioXException("MICROPYTHON_CODE", "REPL 片段最多 16 KiB，且不能包含协议控制字符。");
        }
        // 普通 raw REPL 没有流控；短块发送并留出解释器处理时间，文件另外按小块传输。
        for (var offset = 0; offset < bytes.Length; offset += 128)
        {
            await session.SendAsync(bytes.AsMemory(offset, Math.Min(128, bytes.Length - offset)), token).ConfigureAwait(false);
            await Task.Delay(10, token).ConfigureAwait(false);
        }
        await session.SendAsync(new byte[] { 4 }, token).ConfigureAwait(false);
        var first = await ReadByteAsync(token).ConfigureAwait(false);
        var second = await ReadByteAsync(token).ConfigureAwait(false);
        if (first != 'O' || second != 'K')
        {
            throw new StudioXException("MICROPYTHON_PROTOCOL", $"raw REPL 确认无效：{first:X2} {second:X2}");
        }
    }

    private async Task ReadPromptAsync(CancellationToken token)
    {
        var prompt = await ReadByteAsync(token).ConfigureAwait(false);
        if (prompt != '>')
        {
            throw new StudioXException("MICROPYTHON_PROTOCOL", $"raw REPL 结束帧无效：{prompt:X2}");
        }
    }

    private async Task<byte[]> ReadUntilAsync(byte[] end, CancellationToken token)
    {
        var bytes = new List<byte>();
        while (bytes.Count < 1024 * 1024)
        {
            bytes.Add(await ReadByteAsync(token).ConfigureAwait(false));
            if (bytes.Count >= end.Length && bytes.TakeLast(end.Length).SequenceEqual(end))
            {
                return bytes.Take(bytes.Count - end.Length).ToArray();
            }
        }
        throw new StudioXException("MICROPYTHON_OUTPUT_LIMIT", "REPL 输出超过 1 MiB，已中止本次会话。");
    }

    private async Task<byte> ReadByteAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (frames.DroppedFrames != 0)
        {
            throw new StudioXException("MICROPYTHON_OVERFLOW", $"REPL 接收丢失 {frames.DroppedFrames} 帧，不能继续解析。");
        }
        while (position >= current.Length)
        {
            var frame = await frames.Reader.ReadAsync(token).ConfigureAwait(false);
            if (frames.DroppedFrames != 0)
            {
                throw new StudioXException("MICROPYTHON_OVERFLOW", "REPL 接收队列溢出。");
            }
            current = frame.Payload.ToArray();
            position = 0;
        }
        return current[position++];
    }
}
