namespace StudioX.Ai8051Isp;

public interface IHidReportTransport
{
    Task Write(byte[] report, CancellationToken token);
    Task<byte[]> Read(int timeoutMs, CancellationToken token);
}
