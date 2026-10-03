namespace StudioX.Application.Distribution;

/// <summary>随 IDE 分发的公开目录与信任锚；不能从目录服务器下载公钥来建立信任。</summary>
public static class TrustedDevelopmentCatalog
{
    public const string Source = "https://raw.githubusercontent.com/XieJunHui9566/MCU-StudioX-Toolchains/main/catalog/catalog.json";
    public const string Repository = "https://github.com/XieJunHui9566/MCU-StudioX-Toolchains";
    public const string Publisher = "XieJunHui9566/MCU-StudioX-Toolchains";
    public const string KeySha256 = "02f415bbe0abdf7ffaf456731187354593de93663a66f3dc4b48bf1be3e7e594";
    internal static bool IsSource(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri == new Uri(Source);
    internal static string PublicKey()
    {
        using var stream = typeof(TrustedDevelopmentCatalog).Assembly.GetManifestResourceStream("StudioX.DevelopmentCatalog.Publisher.pem")
            ?? throw new InvalidOperationException("缺少内置组件目录公钥。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
