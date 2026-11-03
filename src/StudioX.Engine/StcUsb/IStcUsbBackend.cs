namespace StudioX.Engine.StcUsb;

using StudioX.Ai8051Isp;

public interface IStcUsbBackend
{
    IReadOnlyList<HidDevice> Enumerate();
    StcUsbConnection Open(HidDevice device, Action<string, byte[]> trace);
}
