namespace StudioX.Engine;

using StudioX.Foundation;

/// <summary>版本来自受索引的 SDK 文件，不根据目录名或安装者的机器环境猜测。</summary>
public static class EspressifSdkIdentity
{
    public static string DeclaredVersion(ToolsetManifest manifest) => manifest.ComponentVersions?.GetValueOrDefault(manifest.Purpose ?? "") ?? manifest.Version;

    public static async Task ValidateAsync(ResolvedToolset tools, EspressifProjectSettings settings, CancellationToken token = default)
    {
        if (tools.Manifest.Purpose != settings.Framework || DeclaredVersion(tools.Manifest) != settings.SdkVersion)
            throw new StudioXException("ESPRESSIF_TOOLSET", "工程的 SDK 身份与开发环境组件不一致。");
        var sdkRelative = tools.Manifest.ResourceDirectories?.GetValueOrDefault("idf")
            ?? throw new StudioXException("TOOL_RESOURCE", "缺少 SDK 资源声明。");
        var versionRelative = sdkRelative.TrimEnd('/') + "/version.txt";
        var versionFile = PathBoundary.Resolve(tools.RootDirectory, versionRelative);
        if (!tools.Manifest.Sha256.ContainsKey(versionRelative) || !File.Exists(versionFile) || new FileInfo(versionFile).Length > 256)
            throw new StudioXException("ESPRESSIF_SDK_VERSION", "SDK 版本文件缺失、未索引或超出限制。");
        var version = (await File.ReadAllTextAsync(versionFile, token)).Trim().TrimStart('v');
        var expected = settings.Framework == "esp8266-rtos-sdk" ? "3.4" : settings.SdkVersion;
        if (version != expected) throw new StudioXException("ESPRESSIF_SDK_VERSION", $"SDK 实际版本 {version} 与工程声明 {settings.SdkVersion} 不同。");
        if (settings.Framework == "esp-idf" && !tools.Manifest.Sha256.ContainsKey(sdkRelative.TrimEnd('/') + "/components/soc/" + settings.Target + "/include/soc/soc_caps.h"))
            throw new StudioXException("ESPRESSIF_SDK_TARGET", "所选 SDK 不包含工程目标的芯片支持：" + settings.Target);
    }
}
