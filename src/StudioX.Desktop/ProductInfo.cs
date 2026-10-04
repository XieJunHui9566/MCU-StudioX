namespace StudioX.Desktop;

/// <summary>安装包和界面均使用程序集版本，避免升级后仍显示旧版本。</summary>
public static class ProductInfo
{
    public static string Version
    {
        get
        {
            var assembly = typeof(App).Assembly;
            var product = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .Cast<System.Reflection.AssemblyMetadataAttribute>()
                .SingleOrDefault(attribute => attribute.Key == "StudioXProductVersion")?.Value;
            if (!string.IsNullOrWhiteSpace(product))
            {
                return product;
            }
            var version = assembly.GetName().Version!;
            return version.ToString(version.Revision > 0 ? 4 : 3);
        }
    }
    public static string DisplayName => "MCU StudioX " + Version;
    public static string ReleaseBadge => Version.EndsWith("LTS", StringComparison.Ordinal)
        ? "LTS"
        : "PREVIEW";
    public static string PreviewVersion => Version.EndsWith("LTS", StringComparison.Ordinal)
        ? Version
        : Version + " · 预览版";
    public static string ReleaseDescription => Version.EndsWith("LTS", StringComparison.Ordinal)
        ? "LTS 维护版"
        : "预览版";
}
