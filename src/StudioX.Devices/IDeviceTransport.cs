namespace StudioX.Devices;

public interface IDeviceTransport : IAsyncDisposable
{
    string Key { get; }
    bool IsSimulated { get; }
    ValueTask OpenAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken);
    ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
}
