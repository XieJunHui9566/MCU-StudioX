namespace StudioX.Engine;

using System.Text.Json;
using StudioX.Foundation;

/// <summary>检查实际 SDK 配置和已生成的引导程序配置，阻止首次启动写入安全 eFuse 的固件。</summary>
internal static class EspressifFlashSecurity
{
    private static readonly HashSet<string> ActivationOptions = new(StringComparer.Ordinal)
    {
        "CONFIG_SECURE_BOOT", "CONFIG_SECURE_BOOT_ENABLED", "CONFIG_SECURE_BOOT_V1_ENABLED",
        "CONFIG_SECURE_BOOT_V2_ENABLED", "CONFIG_SECURE_BOOT_V2_RSA_ENABLED", "CONFIG_SECURE_BOOT_V2_ECDSA_ENABLED",
        "CONFIG_SECURE_BOOT_FLASH_ENC_KEYS_BURN_TOGETHER", "CONFIG_SECURE_FLASH_ENC_ENABLED",
        "CONFIG_FLASH_ENCRYPTION_ENABLED", "CONFIG_SECURE_FLASH_ENCRYPTION_MODE_DEVELOPMENT",
        "CONFIG_SECURE_FLASH_ENCRYPTION_MODE_RELEASE", "CONFIG_BOOTLOADER_APP_ANTI_ROLLBACK"
    };

    internal static async Task ValidateAsync(string projectRoot, string buildDirectory, CancellationToken token)
    {
        var configurations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PathBoundary.Resolve(projectRoot, "sdkconfig")
        };
        var description = PathBoundary.Resolve(buildDirectory, "project_description.json");
        if (File.Exists(description))
        {
            if (new FileInfo(description).Length > 16 * 1024 * 1024)
            {
                throw Invalid("构建描述过大，无法审核实际 SDK 配置。");
            }
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(description, token));
            if (!json.RootElement.TryGetProperty("config_file", out var field) || field.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(field.GetString()))
            {
                throw Invalid("构建描述缺少实际 config_file，无法确认安全启动与加密设置。");
            }
            var root = EspressifPathIdentity.NormalizePath(projectRoot);
            var actual = EspressifPathIdentity.NormalizePath(Path.GetFullPath(field.GetString()!, projectRoot));
            actual = PathBoundary.Resolve(projectRoot, Path.GetRelativePath(root, actual).Replace('\\', '/'));
            if (!File.Exists(actual))
            {
                throw Invalid("构建使用的 SDK 配置已缺失；请重新编译。");
            }
            configurations.Add(actual);
        }
        // 不依赖 JSON 的 encrypted 标志：普通 BIN 中的 bootloader 也可能在首次复位时永久修改 eFuse。
        configurations.Add(PathBoundary.Resolve(buildDirectory, "config/sdkconfig.h"));
        var bootloader = PathBoundary.Resolve(buildDirectory, "bootloader/config/sdkconfig.h");
        if (File.Exists(description) && !File.Exists(bootloader))
        {
            throw Invalid("缺少构建生成的 bootloader 配置，无法审核首次启动行为。");
        }
        configurations.Add(bootloader);
        foreach (var path in configurations.Where(File.Exists))
        {
            if (new FileInfo(path).Length > 2 * 1024 * 1024)
            {
                throw Invalid("SDK 配置过大，无法审核安全启动与加密设置。");
            }
            foreach (var line in await File.ReadAllLinesAsync(path, token))
            {
                var text = line.Trim();
                string option;
                string value;
                if (text.StartsWith("#define ", StringComparison.Ordinal) || text.StartsWith("#define\t", StringComparison.Ordinal))
                {
                    var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3)
                    {
                        continue;
                    }
                    option = parts[1];
                    value = parts[2];
                }
                else
                {
                    var separator = text.IndexOf('=');
                    if (separator < 0)
                    {
                        continue;
                    }
                    option = text[..separator].Trim();
                    value = text[(separator + 1)..].Trim();
                }
                // _SUPPORTED 等芯片能力不是启用项，不能把普通工程误判为安全启动部署。
                if (ActivationOptions.Contains(option) && value is "y" or "1")
                {
                    throw Invalid($"当前普通固件下载入口拒绝启用 {option} 的工程；复位可能修改安全 eFuse。请使用厂商专用部署流程。");
                }
            }
        }
    }

    private static StudioXException Invalid(string message) => new("ESP_FLASH_LAYOUT", message);
}
