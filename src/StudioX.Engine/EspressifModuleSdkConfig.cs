namespace StudioX.Engine;

using System.Text.Json;
using StudioX.Foundation;

/// <summary>在构建目录生成模块配置，只替换已显式选择的硬件项，再核对 SDK 的实际配置结果。</summary>
internal static class EspressifModuleSdkConfig
{
    internal const string BaseRelativePath = ".build/studiox-module-base.json";
    internal static string RelativePath(EspressifModuleSettings settings) => settings.HasOverrides ? EspressifModuleSettings.GeneratedConfigPath : "sdkconfig";

    internal static async Task<string?> PrepareAsync(string root, EspressifModuleConfiguration module, CancellationToken token)
    {
        var settings = module.Settings;
        if (!settings.HasOverrides)
        {
            return null;
        }
        var original = await SourceConfigurationAsync(root, token);
        if (File.Exists(original) && new FileInfo(original).Length > 2 * 1024 * 1024)
        {
            throw Invalid("用户 sdkconfig 过大。");
        }
        var retained = File.Exists(original) ? await File.ReadAllLinesAsync(original, token) : [];
        var lines = retained.Where(line => !Managed(Key(line), settings)).ToList();
        lines.Add("# MCU StudioX module selection; user sdkconfig remains unchanged.");
        void Set(string key, string value) => lines.Add("CONFIG_" + key + "=" + value);
        void Disable(string key) => lines.Add("# CONFIG_" + key + " is not set");
        if (settings.FlashSizeMb is { } size)
        {
            Set("ESPTOOLPY_FLASHSIZE_" + size + "MB", "y");
        }
        if (settings.FlashMode is { } mode)
        {
            Set("ESPTOOLPY_FLASHMODE_" + mode.ToUpperInvariant(), "y");
            if (module.Project.Espressif!.Target == "esp32s3")
            {
                if (mode == "opi")
                {
                    Set("ESPTOOLPY_OCT_FLASH", "y");
                    Set("ESPTOOLPY_FLASH_SAMPLE_MODE_DTR", "y");
                }
                else
                {
                    Disable("ESPTOOLPY_OCT_FLASH");
                    Set("ESPTOOLPY_FLASH_SAMPLE_MODE_STR", "y");
                }
                Disable("ESPTOOLPY_FLASH_MODE_AUTO_DETECT");
            }
        }
        if (settings.FlashFrequencyMhz is { } frequency)
        {
            Set("ESPTOOLPY_FLASHFREQ_" + frequency + "M", "y");
        }
        if (settings.PsramMode is { } psram)
        {
            if (psram == "disabled")
            {
                Disable("SPIRAM");
            }
            else
            {
                Set("SPIRAM", "y");
                Set("SPIRAM_MODE_" + (psram == "octal" ? "OCT" : psram.ToUpperInvariant()), "y");
                if (module.Project.Espressif!.Target is "esp32" or "esp32s3")
                {
                    Set("SPIRAM_TYPE_AUTO", "y");
                }
                if (psram == "hex")
                {
                    Disable("SPIRAM_USE_8LINE_MODE");
                }
            }
        }
        if (settings.P4RevisionFamily is { } revision)
        {
            if (revision == "legacy")
            {
                Set("ESP32P4_SELECTS_REV_LESS_V3", "y");
                Set("ESP32P4_REV_MIN_100", "y");
            }
            else
            {
                Disable("ESP32P4_SELECTS_REV_LESS_V3");
                Set("ESP32P4_REV_MIN_301", "y");
            }
        }
        if (settings.SingleCore is { } singleCore)
        {
            if (singleCore)
            {
                Set("FREERTOS_UNICORE", "y");
            }
            else
            {
                Disable("FREERTOS_UNICORE");
            }
        }
        await File.WriteAllLinesAsync(PathBoundary.Resolve(root, EspressifModuleSettings.GeneratedConfigPath), lines, token);
        return original;
    }

    private static async Task<string> SourceConfigurationAsync(string root, CancellationToken token)
    {
        var description = PathBoundary.Resolve(root, ".build/project_description.json");
        var managed = PathBoundary.Resolve(root, EspressifModuleSettings.GeneratedConfigPath);
        if (File.Exists(description))
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(description, token));
            if (json.RootElement.TryGetProperty("project_path", out var project) && project.GetString() is { } projectPath &&
                EspressifPathIdentity.AreEqual(projectPath, root) && json.RootElement.TryGetProperty("config_file", out var config) &&
                config.GetString() is { } configPath)
            {
                var canonical = EspressifPathIdentity.NormalizePath(Path.GetFullPath(configPath, root));
                var relative = Path.GetRelativePath(EspressifPathIdentity.NormalizePath(root), canonical).Replace('\\', '/');
                if (!relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathFullyQualified(relative))
                {
                    var actual = PathBoundary.Resolve(root, relative);
                    if (File.Exists(actual) && !EspressifPathIdentity.AreEqual(actual, managed))
                    {
                        await JsonStore.WriteAsync(PathBoundary.Resolve(root, BaseRelativePath), new NativeBase(root, relative), token);
                        return actual;
                    }
                }
            }
        }
        var existing = await ReadBaseAsync(root, token);
        if (existing is not null)
        {
            return existing;
        }
        // 首次配置没有原生描述时只能使用标准入口；构建 hook 会拒绝与此入口不同的未配置自定义 SDKCONFIG。
        return PathBoundary.Resolve(root, "sdkconfig");
    }

    internal static async Task<string?> ReadBaseAsync(string root, CancellationToken token)
    {
        var managed = PathBoundary.Resolve(root, EspressifModuleSettings.GeneratedConfigPath);
        var savedBase = PathBoundary.Resolve(root, BaseRelativePath);
        if (File.Exists(savedBase))
        {
            var source = await JsonStore.ReadAsync<NativeBase>(savedBase, token);
            if (EspressifPathIdentity.AreEqual(source.ProjectDirectory, root))
            {
                var original = PathBoundary.Resolve(root, source.RelativePath);
                if (File.Exists(original) && !EspressifPathIdentity.AreEqual(original, managed))
                {
                    return original;
                }
            }
        }
        return null;
    }

    internal static async Task VerifyAsync(string root, EspressifModuleConfiguration module,
        EspressifFlashLayout? layout, CancellationToken token)
    {
        var settings = module.Settings;
        using var description = JsonDocument.Parse(await File.ReadAllTextAsync(PathBoundary.Resolve(root, ".build/project_description.json"), token));
        if (!description.RootElement.TryGetProperty("config_file", out var field) || field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.GetString()))
        {
            throw Invalid("原生 SDK 没有报告实际配置文件。");
        }
        var expected = EspressifPathIdentity.NormalizePath(Path.GetFullPath(field.GetString()!, root));
        expected = PathBoundary.Resolve(root, Path.GetRelativePath(root, expected).Replace('\\', '/'));
        if (settings.HasOverrides && !EspressifPathIdentity.AreEqual(expected, PathBoundary.Resolve(root, EspressifModuleSettings.GeneratedConfigPath)))
        {
            throw Invalid("原生 SDK 未使用已选模块对应的配置；请重新配置工程。");
        }
        if (!File.Exists(expected) || new FileInfo(expected).Length > 2 * 1024 * 1024)
        {
            throw Invalid("缺少有效的实际 SDK 配置。");
        }
        var values = ReadValues(await File.ReadAllLinesAsync(expected, token));
        void Selected(string key)
        {
            if (values.GetValueOrDefault("CONFIG_" + key) != "y")
            {
                throw Invalid("SDK 没有应用所选硬件项 " + key + "；请检查芯片修订和 SDK 配置依赖。");
            }
        }
        if (settings.FlashSizeMb is { } size)
        {
            Selected("ESPTOOLPY_FLASHSIZE_" + size + "MB");
            if (values.GetValueOrDefault("CONFIG_ESPTOOLPY_FLASHSIZE") != size + "MB")
            {
                throw Invalid("SDK 的实际 Flash 容量与模块选择不一致。");
            }
        }
        if (settings.FlashMode is { } mode)
        {
            Selected("ESPTOOLPY_FLASHMODE_" + mode.ToUpperInvariant());
            if (mode == "opi")
            {
                Selected("ESPTOOLPY_OCT_FLASH");
            }
        }
        if (settings.FlashFrequencyMhz is { } frequency)
        {
            Selected("ESPTOOLPY_FLASHFREQ_" + frequency + "M");
        }
        if (module.Project.Espressif!.Target == "esp32" && values.GetValueOrDefault("CONFIG_SPIRAM") == "y" &&
            (values.GetValueOrDefault("CONFIG_ESPTOOLPY_FLASHFREQ") is not ("40m" or "80m") ||
             values.GetValueOrDefault("CONFIG_SPIRAM_SPEED") is not ("40" or "80")))
        {
            throw Invalid("经典 ESP32 的实际 Flash/PSRAM 频率组合不受 SDK 支持，须使用 Flash 40/80 MHz 与 PSRAM 40/80 MHz 合法组合。");
        }
        if (settings.PsramMode is { } psram)
        {
            if (psram == "disabled")
            {
                if (values.GetValueOrDefault("CONFIG_SPIRAM") == "y")
                {
                    throw Invalid("SDK 仍启用了 PSRAM，没有应用关闭选择。");
                }
            }
            else
            {
                Selected("SPIRAM");
                Selected("SPIRAM_MODE_" + (psram == "octal" ? "OCT" : psram.ToUpperInvariant()));
                if (psram == "hex" && values.GetValueOrDefault("CONFIG_SPIRAM_USE_8LINE_MODE") == "y")
                {
                    throw Invalid("P4 实际启用了 8 线模式，与选定的 16 线 HEX 规格不符。");
                }
            }
        }
        if (settings.P4RevisionFamily is { } revision)
        {
            if (revision == "legacy")
            {
                Selected("ESP32P4_SELECTS_REV_LESS_V3");
                Selected("ESP32P4_REV_MIN_100");
            }
            else
            {
                Selected("ESP32P4_REV_MIN_301");
                if (values.GetValueOrDefault("CONFIG_ESP32P4_SELECTS_REV_LESS_V3") == "y")
                {
                    throw Invalid("P4 芯片修订系列与模块选择不一致。");
                }
            }
        }
        if (settings.SingleCore is { } singleCore && (values.GetValueOrDefault("CONFIG_FREERTOS_UNICORE") == "y") != singleCore)
        {
            throw Invalid("SDK 的单核/双核配置与模块选择不一致。");
        }
        if (layout is not null)
        {
            if (layout.FlashSize != values.GetValueOrDefault("CONFIG_ESPTOOLPY_FLASHSIZE") ||
                layout.FlashMode != values.GetValueOrDefault("CONFIG_ESPTOOLPY_FLASHMODE") ||
                layout.FlashFrequency != values.GetValueOrDefault("CONFIG_ESPTOOLPY_FLASHFREQ"))
            {
                throw Invalid("下载布局与实际 SDK 的 Flash 容量、引导模式或频率不一致。");
            }
            // QIO/QOUT 和 OPI 会以 SDK 指定的 DIO/DOUT 引导头下载；不能把 ROM 引导模式误当应用总线模式。
            var bootMode = settings.FlashMode switch
            {
                "qio" or "qout" => "dio",
                "opi" => "dout",
                var other => other
            };
            if (bootMode is not null && layout.FlashMode != bootMode ||
                settings.FlashSizeMb is { } declared && EspressifFlashLayoutReader.FlashCapacity(layout.FlashSize) != (ulong)declared * 1024 * 1024)
            {
                throw Invalid("下载布局没有应用当前模块规格。");
            }
        }
    }

    private static Dictionary<string, string> ReadValues(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var separator = line.IndexOf('=');
            if (separator <= 0 || !line.StartsWith("CONFIG_", StringComparison.Ordinal))
            {
                continue;
            }
            var key = line[..separator];
            var value = line[(separator + 1)..].Trim('"');
            // IDF 6.x 的兼容项可能重复输出；只接受相同值，冲突值仍须拒绝。
            if (!values.TryAdd(key, value) && values[key] != value)
            {
                throw Invalid("实际 SDK 配置含有冲突的重复项 " + key + "。");
            }
        }
        return values;
    }

    private static bool Managed(string key, EspressifModuleSettings settings) =>
        settings.FlashSizeMb is not null && key.StartsWith("CONFIG_ESPTOOLPY_FLASHSIZE", StringComparison.Ordinal) ||
        settings.FlashFrequencyMhz is not null && key.StartsWith("CONFIG_ESPTOOLPY_FLASHFREQ", StringComparison.Ordinal) ||
        settings.FlashMode is not null && (key.StartsWith("CONFIG_ESPTOOLPY_FLASHMODE", StringComparison.Ordinal) ||
            key.StartsWith("CONFIG_ESPTOOLPY_FLASH_SAMPLE_MODE", StringComparison.Ordinal) || key is "CONFIG_ESPTOOLPY_OCT_FLASH" or "CONFIG_ESPTOOLPY_FLASH_MODE_AUTO_DETECT") ||
        settings.PsramMode is not null && (key is "CONFIG_SPIRAM" or "CONFIG_SPIRAM_USE_8LINE_MODE" ||
            key.StartsWith("CONFIG_SPIRAM_MODE_", StringComparison.Ordinal) || key.StartsWith("CONFIG_SPIRAM_TYPE_", StringComparison.Ordinal)) ||
        settings.P4RevisionFamily is not null && (key.StartsWith("CONFIG_ESP32P4_REV_MIN", StringComparison.Ordinal) ||
            key is "CONFIG_ESP32P4_SELECTS_REV_LESS_V3" or "CONFIG_ESP_REV_MIN_FULL" or "CONFIG_ESP_REV_MIN") ||
        settings.SingleCore is not null && key == "CONFIG_FREERTOS_UNICORE";

    private static string Key(string line)
    {
        var text = line.Trim();
        if (text.StartsWith("# CONFIG_", StringComparison.Ordinal))
        {
            return text[2..].Split(' ', 2)[0];
        }
        return text.Split('=', 2)[0];
    }

    private static StudioXException Invalid(string message) => new("ESP_MODULE_CONFIG", message);
    private sealed record NativeBase(string ProjectDirectory, string RelativePath);
}
