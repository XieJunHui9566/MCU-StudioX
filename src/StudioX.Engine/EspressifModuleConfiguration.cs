namespace StudioX.Engine;

using System.Text.Json;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>独立保存和核对模块选择；不改写用户的 CMake、sdkconfig 或默认配置。</summary>
internal sealed record EspressifModuleConfiguration(ProjectManifest Project, DeviceDefinition Device, EspressifModuleSettings Settings)
{
    internal ulong DeclaredFlashBytes => Settings.FlashSizeMb is { } size ? (ulong)size * 1024 * 1024 : Device.FlashBytes;

    internal static async Task<EspressifModuleConfiguration> ReadAsync(string root, CancellationToken token, bool loadSettings = true)
    {
        var project = await ProjectService.ReadAsync(root, token);
        var sdk = project.Espressif ?? throw Invalid("当前工程不是原生 Espressif SDK 工程。");
        var pack = await JsonStore.ReadAsync<PackManifest>(PathBoundary.Resolve(root, "device/manifest.json"), token);
        var device = pack.Devices.Single(item => item.Id == project.DeviceId);
        if (device.Espressif is not { } declared || declared.Target != sdk.Target || declared.Framework != sdk.Framework || declared.SdkVersion != sdk.SdkVersion)
        {
            throw Invalid("工程与器件包锁定的 SDK 或芯片不一致。");
        }
        var settings = loadSettings ? await ReadSettingsAsync(root, token) : new();
        Validate(settings, sdk.Target, device.FlashBytes);
        return new(project, device, settings);
    }

    internal static async Task<EspressifModuleSettings> ReadSettingsAsync(string root, CancellationToken token)
    {
        var path = PathBoundary.Resolve(root, EspressifModuleSettings.RelativePath);
        if (!File.Exists(path)) { return new(); }
        if (new FileInfo(path).Length is <= 0 or > 16 * 1024) { throw Invalid("模块设置文件无效或过大。"); }
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
        if (json.RootElement.ValueKind != JsonValueKind.Object) { throw Invalid("模块设置必须为 JSON 对象。"); }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in json.RootElement.EnumerateObject())
        {
            if (!names.Add(field.Name)) { throw Invalid("模块设置包含重复字段。"); }
        }
        return json.RootElement.Deserialize<EspressifModuleSettings>(JsonStore.Options) ?? throw Invalid("模块设置为空。");
    }

    internal static void Validate(EspressifModuleSettings settings, string target, ulong originalFlashBytes)
    {
        if (settings.FormatVersion != 1) { throw Invalid("不支持此模块设置格式版本。"); }
        var capabilities = EspressifModuleCapabilities.ForTarget(target);
        if (settings.FlashSizeMb is { } size && !capabilities.FlashSizesMb.Contains(size) ||
            settings.FlashMode is { } mode && !capabilities.FlashModes.Contains(mode, StringComparer.Ordinal) ||
            settings.FlashFrequencyMhz is { } frequency && !capabilities.FlashFrequenciesMhz.Contains(frequency) ||
            settings.PsramMode is { } psram && !capabilities.PsramModes.Contains(psram, StringComparer.Ordinal))
        {
            throw Invalid("所选容量、Flash 模式、频率或 PSRAM 模式不受当前芯片的内置 SDK 支持。");
        }
        if (settings.P4RevisionFamily is { } revision && (capabilities.P4RevisionFamilies is null || !capabilities.P4RevisionFamilies.Contains(revision, StringComparer.Ordinal)) ||
            settings.SingleCore is not null && !capabilities.SupportsSingleCore)
        {
            throw Invalid("所选芯片修订系列或核心数量不适用于当前目标。");
        }
        if (target == "esp32" && settings.PsramMode == "quad" && settings.FlashFrequencyMhz is not (null or 40 or 80))
        {
            throw Invalid("经典 ESP32 启用 PSRAM 时，SDK 仅支持 40 或 80 MHz Flash。");
        }
        if (settings.PsramSizeMb is { } psramSize && !SupportedPsramSize(settings.PsramMode, psramSize))
        {
            throw Invalid("PSRAM 容量需要明确的总线模式，且须为有效的板卡声明容量。");
        }
        if (settings.ProfileId is { } id)
        {
            var profile = EspressifModuleCatalog.ForTarget(target).SingleOrDefault(item => item.Id == id);
            if (profile is null || profile.Settings != settings || profile.Target != target)
            {
                throw Invalid("模块预设的目标或硬件参数不一致；请重新选择准确预设，或明确使用自定义设置。");
            }
        }
        else if (originalFlashBytes > 0 && settings.FlashSizeMb is { } custom && (ulong)custom * 1024 * 1024 > originalFlashBytes)
        {
            throw Invalid("自定义 Flash 容量超过器件包已核实的容量；请使用有官方证据的准确模组预设。");
        }
    }

    // 对应锁定 SDK 的 quad/octal/hex 驱动所识别的 density；这是声明范围，实际容量仍由开机检测。
    private static bool SupportedPsramSize(string? mode, int size) => mode switch
    {
        "quad" => size is 2 or 4 or 8,
        "octal" or "hex" => size is 4 or 8 or 16 or 32 or 64,
        _ => false
    };

    private static StudioXException Invalid(string message) => new("ESP_MODULE_SETTINGS", message);
}
