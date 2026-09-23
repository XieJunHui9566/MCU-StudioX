namespace StudioX.Devices;

using System.Threading.Channels;

public sealed class FrameSubscription : IDisposable
{
    private readonly Channel<DeviceFrame> channel;
    private readonly Action<FrameSubscription> remove;
    private long dropped;
    private int disposed;
    internal FrameSubscription(int capacity, Action<FrameSubscription> remove)
    {
        this.remove = remove;
        channel = Channel.CreateBounded<DeviceFrame>(new BoundedChannelOptions(capacity)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true, SingleReader = false, AllowSynchronousContinuations = false },
            _ => Interlocked.Increment(ref dropped));
    }
    public ChannelReader<DeviceFrame> Reader => channel.Reader;
    public long DroppedFrames => Interlocked.Read(ref dropped);
    internal void Publish(DeviceFrame frame) => channel.Writer.TryWrite(frame);
    internal void Complete(Exception? error = null) => channel.Writer.TryComplete(error);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        remove(this);
        Complete();
    }
}
