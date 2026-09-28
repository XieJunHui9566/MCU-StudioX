namespace StudioX.Desktop;

using System.Text;

/// <summary>接收线程只写有界缓存，界面定时取出；高频打印不能堆积 Dispatcher 消息。</summary>
internal sealed class MicroPythonOutputBuffer
{
    private const int Capacity = 128 * 1024;
    private readonly object sync = new();
    private readonly StringBuilder pending = new();
    private long discarded;

    public void Append(string text)
    {
        lock (sync)
        {
            var excess = Math.Max(0, pending.Length + text.Length - Capacity);
            discarded += excess;
            var fromPending = Math.Min(excess, pending.Length);
            pending.Remove(0, fromPending);
            pending.Append(text.AsSpan(excess - fromPending));
        }
    }

    public (string Text, long Discarded) Drain()
    {
        lock (sync)
        {
            var result = (pending.ToString(), discarded);
            pending.Clear();
            discarded = 0;
            return result;
        }
    }
}
