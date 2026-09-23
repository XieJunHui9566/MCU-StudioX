namespace StudioX.Devices;

public sealed class DeviceFrame(long sequence, long timestamp, bool simulated, ReadOnlyMemory<byte> bytes)
{
    private readonly byte[] payload = bytes.ToArray();
    public long Sequence { get; } = sequence;
    public long Timestamp { get; } = timestamp;
    public DateTimeOffset ReceivedAt { get; } = DateTimeOffset.UtcNow;
    public bool IsSimulated { get; } = simulated;
    public string ClockDomain => "host-monotonic";
    public ReadOnlyMemory<byte> Payload => payload;
}
