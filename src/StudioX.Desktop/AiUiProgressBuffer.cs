namespace StudioX.Desktop;

using StudioX.Application;

/// <summary>把后台流式事件交给桌面定时器批量消费，避免每个文本片段阻塞界面线程。</summary>
internal sealed class AiUiProgressBuffer : IProgress<AiAgentProgress>
{
    private readonly object gate = new();
    private readonly Queue<AiAgentProgress> pending = new();
    private bool closed;

    public void Report(AiAgentProgress value)
    {
        lock (gate)
        {
            if (!closed)
            {
                pending.Enqueue(value);
            }
        }
    }

    public void Close()
    {
        lock (gate)
        {
            closed = true;
            pending.Clear();
        }
    }

    public AiAgentProgress[] Drain()
    {
        lock (gate)
        {
            if (pending.Count == 0)
            {
                return [];
            }
            var updates = pending.ToArray();
            pending.Clear();
            return updates;
        }
    }
}
