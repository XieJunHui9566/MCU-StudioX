namespace StudioX.Engine;

using StudioX.Ai8051Isp;

/// <summary>仅测试可替换传输；产品组合根始终使用系统 HID 与现有进程间设备租约。</summary>
public interface IMx251IspBackend
{
    IReadOnlyList<HidDevice> Enumerate();
    Mx251IspConnection Open(HidDevice device, Action<string, byte[]> trace);
}
