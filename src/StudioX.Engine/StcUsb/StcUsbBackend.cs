namespace StudioX.Engine.StcUsb;

using StudioX.Ai8051Isp;

/// <summary>只发现原生 USB Boot ROM，不枚举或借用 FF0F 串口下载桥。</summary>
public sealed class StcUsbBackend : IStcUsbBackend
{
    public IReadOnlyList<HidDevice> Enumerate() => HidTransport.Enumerate(0x34bf, 0x1001);

    public StcUsbConnection Open(HidDevice device, Action<string, byte[]> trace)
    {
        var transport = new HidTransport(device, trace);
        return new(transport, transport);
    }
}
