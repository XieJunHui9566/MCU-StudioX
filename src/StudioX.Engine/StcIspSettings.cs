namespace StudioX.Engine;

using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>STC 串口 ISP 选项按工程保存；时钟选项只在明确下载时写入芯片。</summary>
public sealed record StcIspSettings(int FormatVersion = 1, string Port = "",
    StcClockMode ClockMode = StcClockMode.Preserve, int? ClockFrequencyHz = null,
    int TransferBaud = 115200)
{
    public const string RelativePath = ".studiox/stc-isp.json";

    public static async Task<StcIspSettings> ReadAsync(string root, CancellationToken token = default)
    {
        var path = PathBoundary.Resolve(root, RelativePath);
        var value = File.Exists(path) ? await JsonStore.ReadAsync<StcIspSettings>(path, token) : new();
        value.Validate();
        return value;
    }

    public void Validate()
    {
        if (FormatVersion != 1 || !Enum.IsDefined(ClockMode) || TransferBaud is < 1200 or > 921600 ||
            (!string.IsNullOrEmpty(Port) && !Regex.IsMatch(Port, @"^COM[1-9][0-9]{0,3}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) ||
            ClockFrequencyHz is < 1000000 or > 40000000 ||
            ClockMode == StcClockMode.Preserve && ClockFrequencyHz is not null)
        {
            throw new StudioXException("STC_ISP_SETTINGS", "STC ISP 设置无效；请选择 COM 串口、有效速度和受支持的时钟模式/频率。");
        }
    }

    public void ValidateFor(StcIspCapabilities capabilities, bool requirePort = false)
    {
        Validate();
        if (requirePort && string.IsNullOrWhiteSpace(Port))
        {
            throw new StudioXException("STC_ISP_PORT", "请先选择开发板使用的 COM 串口。");
        }
        if (!capabilities.SupportedClockModes.Contains(ClockMode))
        {
            throw new StudioXException("STC_ISP_CLOCK", $"{capabilities.DeviceId} 的 ISP 工具不支持所选时钟模式。");
        }
        if (ClockMode == StcClockMode.InternalRc && ClockFrequencyHz is { } rc)
        {
            if (!capabilities.SupportsRcTrim || rc < capabilities.MinRcFrequencyHz || rc > capabilities.MaxRcFrequencyHz)
            {
                throw new StudioXException("STC_ISP_CLOCK", $"{capabilities.DeviceId} 的 RC 频率需在 {capabilities.MinRcFrequencyHz / 1000000m}–{capabilities.MaxRcFrequencyHz / 1000000m} MHz 范围内。");
            }
        }
        if (ClockMode == StcClockMode.ExternalCrystal && ClockFrequencyHz is null)
        {
            throw new StudioXException("STC_ISP_CLOCK", "选择外部晶振时须填写板上实装晶振频率；ISP 不会改变物理晶振。");
        }
    }
}
