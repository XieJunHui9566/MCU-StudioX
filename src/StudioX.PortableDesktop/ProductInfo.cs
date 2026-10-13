namespace StudioX.PortableDesktop;

using System.Reflection;

/// <summary>读取同一构建版本元数据，避免 Linux 界面保留写死的旧版本。</summary>
internal static class ProductInfo
{
    public static string DisplayName => "MCU StudioX " + (typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .SingleOrDefault(attribute => attribute.Key == "StudioXProductVersion")?.Value
        ?? typeof(App).Assembly.GetName().Version?.ToString() ?? "未知版本");
}
