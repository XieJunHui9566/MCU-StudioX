namespace StudioX.Desktop;

using System.Windows.Media;
using System.Windows.Media.Imaging;

/// <summary>厂商展示信息独立于包标识；只为已核实的厂商提供中文名和内置商标。</summary>
internal sealed record ManufacturerOption(string Id, string DisplayName, string EnglishName, ImageSource? Logo)
{
    public string Description => DisplayName == EnglishName ? DisplayName : $"{DisplayName} · {EnglishName}";
    public string Monogram => Id.Length <= 3 ? Id.ToUpperInvariant() : Id[..2].ToUpperInvariant();
    // 官方横版标识需要保留字标的可读宽度，其他厂商延续原有布局。
    public double LogoFrameWidth => Id.Equals("Espressif", StringComparison.OrdinalIgnoreCase) ? 80 : 48;

    public static ManufacturerOption FromId(string id)
    {
        if (id.Equals("Espressif", StringComparison.OrdinalIgnoreCase))
        {
            return new(id, "乐鑫科技", "Espressif", (ImageSource)System.Windows.Application.Current.FindResource("EspressifLogo"));
        }
        if (id.Equals("STMicroelectronics", StringComparison.OrdinalIgnoreCase))
        {
            return new(id, "意法半导体", "STMicroelectronics", LoadLogo("st.png"));
        }
        if (id.Equals("WCH", StringComparison.OrdinalIgnoreCase))
        {
            return new(id, "沁恒微电子", "WCH", LoadLogo("wch.png"));
        }
        if (id.Equals("Puya", StringComparison.OrdinalIgnoreCase))
        {
            return new(id, "普冉半导体", "Puya", LoadLogo("puya.png"));
        }
        if (id.Equals("GigaDevice", StringComparison.OrdinalIgnoreCase))
        {
            return new(id, "兆易创新", "GigaDevice", LoadLogo("gigadevice.png"));
        }
        if (id.Equals("STC", StringComparison.OrdinalIgnoreCase) ||
            id.Equals("STC / 宏晶科技", StringComparison.OrdinalIgnoreCase))
        {
            return new("STC", "宏晶科技", "STC", null);
        }
        if (id.Equals("Raspberry Pi", StringComparison.OrdinalIgnoreCase))
        {
            return new(id, "树莓派", "Raspberry Pi", LoadLogo("raspberrypi.ico"));
        }
        if (id.Equals("AGM", StringComparison.OrdinalIgnoreCase) || id.Equals("AGM Micro", StringComparison.OrdinalIgnoreCase))
        {
            return new(id, "遨格芯", "AGM Micro", LoadLogo("agm.jpg"));
        }
        return new(id, id, id, null);
    }

    private static ImageSource LoadLogo(string file)
    {
        // 只加载发行版内的静态资源，显示厂商时无需网络或读取包中可执行内容。
        var image = new BitmapImage(new Uri($"pack://application:,,,/Assets/Manufacturers/{file}", UriKind.Absolute));
        image.Freeze();
        return image;
    }
}
