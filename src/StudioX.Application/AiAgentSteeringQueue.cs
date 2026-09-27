namespace StudioX.Application;

using StudioX.Foundation;

/// <summary>运行中提示词的线程安全收件箱。被 Agent 消费前仍可在任务结束后取回。</summary>
public sealed class AiAgentSteeringQueue
{
    private readonly object gate = new();
    private readonly Queue<string> pending = new();
    private bool accepting = true;

    public event Action<string>? MessageDequeued;

    public bool IsAccepting
    {
        get
        {
            lock (gate)
            {
                return accepting;
            }
        }
    }
    public int PendingCount
    {
        get
        {
            lock (gate)
            {
                return pending.Count;
            }
        }
    }
    public IReadOnlyList<string> PendingMessages
    {
        get
        {
            lock (gate)
            {
                return pending.ToArray();
            }
        }
    }

    public bool TryEnqueue(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4_000 || text.Contains('\0'))
        {
            throw new StudioXException("AI_PROMPT_SIZE", "运行中提示词不能为空，且不能超过 4000 个字符。");
        }
        lock (gate)
        {
            if (!accepting)
            {
                return false;
            }
            pending.Enqueue(text);
            return true;
        }
    }

    /// <summary>任务取消或失败后，宿主可取回尚未消费的提示词。</summary>
    public IReadOnlyList<string> ClearPending()
    {
        lock (gate)
        {
            var result = pending.ToArray();
            pending.Clear();
            return result;
        }
    }

    public IReadOnlyList<string> DrainPending() => ClearPending();

    internal IReadOnlyList<string> Consume() => ClearPending();

    internal void NotifyDequeued(string message)
    {
        if (MessageDequeued is not { } callbacks)
        {
            return;
        }
        foreach (Action<string> callback in callbacks.GetInvocationList())
        {
            // UI 通知不可改变 Agent 已接受提示词后的协议状态。
            try
            {
                callback(message);
            }
            catch (Exception) { }
        }
    }

    internal bool TryCloseIfEmpty()
    {
        lock (gate)
        {
            if (pending.Count != 0)
            {
                return false;
            }
            accepting = false;
            return true;
        }
    }

    internal void Close()
    {
        lock (gate)
        {
            accepting = false;
        }
    }
}
