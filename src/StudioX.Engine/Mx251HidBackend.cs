namespace StudioX.Engine;

using StudioX.Ai8051Isp;
public sealed class Mx251HidBackend : IMx251IspBackend
{
    public IReadOnlyList<HidDevice> Enumerate() => HidTransport.Enumerate();
    public Mx251IspConnection Open(HidDevice device, Action<string, byte[]> trace)
    {
        var transport = new HidTransport(device, trace);
        return new(transport, transport);
    }
}
