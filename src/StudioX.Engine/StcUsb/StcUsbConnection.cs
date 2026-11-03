namespace StudioX.Engine.StcUsb;

using StudioX.Ai8051Isp;

public sealed record StcUsbConnection(IHidReportTransport Transport, IDisposable Lifetime) : IDisposable
{
    public void Dispose() => Lifetime.Dispose();
}
