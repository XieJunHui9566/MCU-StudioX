namespace StudioX.Engine;

using System.Text.RegularExpressions;
using StudioX.Foundation;
using StudioX.Packages;

public enum StcClockMode { Preserve, InternalRc, ExternalCrystal }

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
            throw new StudioXException("STC_ISP_SETTINGS", "STC ISP 设置无效；请选择 COM 串口、有效速度和受支持的时钟模式/频率。");
    }

    public void ValidateFor(StcIspCapabilities capabilities, bool requirePort = false)
    {
        Validate();
        if (requirePort && string.IsNullOrWhiteSpace(Port))
            throw new StudioXException("STC_ISP_PORT", "请先选择开发板使用的 COM 串口。");
        if (!capabilities.SupportedClockModes.Contains(ClockMode))
            throw new StudioXException("STC_ISP_CLOCK", $"{capabilities.DeviceId} 的 ISP 工具不支持所选时钟模式。");
        if (ClockMode == StcClockMode.InternalRc && ClockFrequencyHz is { } rc)
        {
            if (!capabilities.SupportsRcTrim || rc < capabilities.MinRcFrequencyHz || rc > capabilities.MaxRcFrequencyHz)
                throw new StudioXException("STC_ISP_CLOCK", $"{capabilities.DeviceId} 的 RC 频率需在 {capabilities.MinRcFrequencyHz / 1000000m}–{capabilities.MaxRcFrequencyHz / 1000000m} MHz 范围内。");
        }
        if (ClockMode == StcClockMode.ExternalCrystal && ClockFrequencyHz is null)
            throw new StudioXException("STC_ISP_CLOCK", "选择外部晶振时须填写板上实装晶振频率；ISP 不会改变物理晶振。");
    }
}

public sealed record StcIspCapabilities(string DeviceId, IReadOnlyList<StcClockMode> SupportedClockModes,
    bool SupportsRcTrim, int MinRcFrequencyHz, int MaxRcFrequencyHz, string Warning)
{
    public static StcIspCapabilities For(DeviceDefinition device)
    {
        if (device.Architecture != "mcs51" || device.ToolsetId != "stc.sdcc")
            throw new StudioXException("STC_ISP_DEVICE", "仅 STC SDCC 工程支持串口 ISP。");
        var id = device.Id.ToUpperInvariant();
        if (id.StartsWith("STC15", StringComparison.Ordinal) || id.StartsWith("IAP15", StringComparison.Ordinal))
            return new(device.Id, [StcClockMode.Preserve, StcClockMode.InternalRc, StcClockMode.ExternalCrystal],
                true, 5000000, 28000000, "外部晶振必须实装并与填写频率一致；切换后若无时钟，程序无法正常运行。stcgal 不会设置晶振物理频率。" );
        if (id.StartsWith("STC12", StringComparison.Ordinal))
            return new(device.Id, [StcClockMode.Preserve, StcClockMode.InternalRc, StcClockMode.ExternalCrystal],
                false, 0, 0, "该系列的 stcgal 只支持切换内/外部时钟，不支持 RC 频率校准；外部晶振需已实装。" );
        if (id.StartsWith("STC8G", StringComparison.Ordinal) || id.StartsWith("STC8H", StringComparison.Ordinal))
            return new(device.Id, [StcClockMode.Preserve, StcClockMode.InternalRc],
                true, 4000000, 36000000, "stcgal 1.10 未提供该系列的外部时钟源配置；可选择内部 RC 校准。" );
        if (id.StartsWith("STC89", StringComparison.Ordinal))
            return new(device.Id, [StcClockMode.Preserve], false, 0, 0,
                "stcgal 1.10 未提供该系列的 RC 校准或内/外部时钟切换。" );
        throw new StudioXException("STC_ISP_DEVICE", "当前 STC 型号尚无经过核对的 ISP 时钟能力。");
    }
}
