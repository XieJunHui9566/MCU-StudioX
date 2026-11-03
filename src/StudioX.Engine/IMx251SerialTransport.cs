namespace StudioX.Engine;

public interface IMx251SerialTransport : IDisposable
{
    void SetBaudRate(int baud);
    Task WriteAsync(byte[] bytes, CancellationToken token);
    Task<byte[]> ReadAsync(int timeoutMs, CancellationToken token);
}
public interface IMx251SerialBackend
{
    string[] GetPortNames();
    IMx251SerialTransport Open(string port, Action<string, byte[]> trace);
}
