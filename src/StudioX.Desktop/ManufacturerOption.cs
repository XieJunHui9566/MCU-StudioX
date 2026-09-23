namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

/// <summary>厂商展示信息独立于包标识；缺少已授权的图像资源时使用文字缩写。</summary>
internal sealed record ManufacturerOption(string Id, string DisplayName, string EnglishName, ImageSource? Logo)
{
    public string Description => DisplayName == EnglishName ? DisplayName : $"{DisplayName} · {EnglishName}";
    public string Monogram => Id.Length <= 3 ? Id.ToUpperInvariant() : Id[..2].ToUpperInvariant();

    public static ManufacturerOption FromId(string id)
    {
        if (id.Equals("STMicroelectronics", StringComparison.OrdinalIgnoreCase))
            return new(id, "意法半导体", "STMicroelectronics", LoadLogo("st.png"));
        if (id.Equals("WCH", StringComparison.OrdinalIgnoreCase))
            return new(id, "沁恒微电子", "WCH", LoadLogo("wch.png"));
        if (id.Equals("Puya", StringComparison.OrdinalIgnoreCase))
            return new(id, "普冉半导体", "Puya", LoadLogo("puya.png"));
        if (id.Equals("GigaDevice", StringComparison.OrdinalIgnoreCase))
            return new(id, "兆易创新", "GigaDevice", LoadLogo("gigadevice.png"));
        if (id.Equals("STC", StringComparison.OrdinalIgnoreCase) ||
            id.Equals("STC / 宏晶科技", StringComparison.OrdinalIgnoreCase))
            return new("STC", "宏晶科技", "STC", null);
        if (id.Equals("Raspberry Pi", StringComparison.OrdinalIgnoreCase))
            return new(id, "树莓派", "Raspberry Pi", LoadLogo("raspberrypi.ico"));
        if (id.Equals("AGM", StringComparison.OrdinalIgnoreCase) || id.Equals("AGM Micro", StringComparison.OrdinalIgnoreCase))
            return new(id, "遨格芯", "AGM Micro", LoadLogo("agm.jpg"));
        return new(id, id, id, null);
    }

    private static ImageSource? LoadLogo(string file)
    {
        // 公开源码不附带厂商商标；缺少资源时让界面使用 Monogram 回退。
        var uri = new Uri($"pack://application:,,,/Assets/Manufacturers/{file}", UriKind.Absolute);
        try
        {
            var resource = Application.GetResourceStream(uri);
            if (resource is null) return null;
            using var stream = resource.Stream;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (System.IO.IOException)
        {
            return null;
        }
    }
}
