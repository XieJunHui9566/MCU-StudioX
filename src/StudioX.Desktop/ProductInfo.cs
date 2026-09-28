namespace StudioX.Desktop;

/// <summary>安装包和界面均使用程序集版本，避免升级后仍显示旧版本。</summary>
public static class ProductInfo
{
    public static string Version
    {
        get
        {
            var version = typeof(App).Assembly.GetName().Version!;
            return version.ToString(version.Revision > 0 ? 4 : 3);
        }
    }
    public static string DisplayName => "MCU StudioX " + Version;
    public static string PreviewVersion => Version + " · 预览版";
}
