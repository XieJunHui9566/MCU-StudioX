namespace StudioX.Engine;

using StudioX.Ai8051Isp;

public sealed record Mx251IspSettings(string DevicePath = "", int CpuMode = 32, int IrcHz = 40000000)
{
    public void Validate(bool requireDevice = false)
    {
        new DeviceConfiguration(1, "AI8051U-34K64", CpuMode, IrcHz).Validate();
        if (requireDevice && string.IsNullOrWhiteSpace(DevicePath))
        {
            throw new StudioX.Foundation.StudioXException("MX251_ISP_DEVICE", "请在工程详情中选择 AI8H2K12G USB 下载器并保存。");
        }
    }
}
