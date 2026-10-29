namespace StudioX.Engine;

using StudioX.Ai8051Isp;
public sealed record Mx251IspConnection(IHidReportTransport Transport, IDisposable Lifetime) : IDisposable
{
    public void Dispose() => Lifetime.Dispose();
}
