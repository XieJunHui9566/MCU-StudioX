namespace StudioX.Engine;

using StudioX.Ai8051Isp;

public enum Mx251DownloadMode { UsbBridge, NativeUsb, Serial }

public sealed record Mx251IspSettings(string DevicePath = "", int CpuMode = 32, int IrcHz = 40000000,
    Mx251DownloadMode DownloadMode = Mx251DownloadMode.UsbBridge, string SerialPort = "")
{
    public void Validate(bool requireDevice = false)
    {
        new DeviceConfiguration(1, "AI8051U-34K64", CpuMode, IrcHz).Validate();
        if (!Enum.IsDefined(DownloadMode)) throw new StudioX.Foundation.StudioXException("MX251_ISP_MODE", "未知下载方式。");
        if (requireDevice && DownloadMode == Mx251DownloadMode.Serial && !System.Text.RegularExpressions.Regex.IsMatch(SerialPort, @"^COM[1-9][0-9]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new StudioX.Foundation.StudioXException("MX251_ISP_PORT", "请选择串口下载使用的 COM 端口。");
        if (requireDevice && DownloadMode == Mx251DownloadMode.UsbBridge && string.IsNullOrWhiteSpace(DevicePath))
        {
            throw new StudioX.Foundation.StudioXException("MX251_ISP_DEVICE", "请在工程详情中选择 AI8H2K12G USB 下载器并保存。");
        }
    }
}
