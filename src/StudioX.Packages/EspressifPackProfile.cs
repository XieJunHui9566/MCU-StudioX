namespace StudioX.Packages;

using StudioX.Foundation;

/// <summary>约束已接入的 SDK 和目标，防止器件包把其他架构误送入 IDF 构建流程。</summary>
public static class EspressifPackProfile
{
    public static void ValidateFramework(EspressifDeviceDefinition profile)
    {
        var supported = profile.Framework switch
        {
            // 原生契约按 5.x/6.x 分开验收；接受精确发行身份，不把未知主版本交给已有适配器。
            "esp-idf" => System.Text.RegularExpressions.Regex.IsMatch(profile.SdkVersion, @"\A[56]\.[0-9]+\.[0-9]+\z") &&
                profile.Target is "esp32" or "esp32p4" or "esp32s3" or "esp32c3" or "esp32c5" or "esp32c6",
            "esp8266-rtos-sdk" => profile.SdkVersion == "3.4.0" && profile.Target == "esp8266",
            _ => false
        };
        if (!supported)
        {
            throw new StudioXException("PACK_ESPRESSIF_SDK", "Espressif SDK 版本或构建目标不受支持；IDF 5.x/6.x 必须指定完整发行版本，ESP8266 使用独立的 RTOS SDK 3.4.0。");
        }
    }

    public static string ToolsetId(string framework) => framework == "esp-idf"
        ? "espressif.idf" : "espressif.esp8266-rtos";

    public static string CompilerId(string framework) => framework == "esp-idf" ? "esp-idf" : "esp8266-rtos";

    public static void Validate(DeviceDefinition device)
    {
        var profile = device.Espressif ??
            throw new StudioXException("PACK_ESPRESSIF_SDK", "Espressif 器件缺少明确的 SDK 与构建目标配置。");
        ValidateFramework(profile);
        var architecture = profile.Target is "esp32" or "esp32s3" or "esp8266" ? "xtensa" : "riscv";
        if (device.Architecture != architecture || device.ToolsetId != ToolsetId(profile.Framework) ||
            device.CompilerId != CompilerId(profile.Framework))
        {
            throw new StudioXException("PACK_ESPRESSIF_TOOLSET", "Espressif 架构、SDK 与工具锁定信息不一致。");
        }
        // SDK 拥有启动、链接、CPU 参数和 Flash 分区；不能静默忽略包里额外声明的裸机构建参数。
        if (device.LinkerScript != "" || device.OpenOcd is not null ||
            device.CpuFlags is not { Count: 0 } || device.Defines is not { Count: 0 } ||
            device.IncludeDirectories is not { Count: 0 } || device.Sources is not { Count: 0 } ||
            device.CompileOptions is not { Count: 0 } || device.LinkOptions is not { Count: 0 } ||
            device.Templates is null || device.Templates.Any(template => template.Build is not null ||
                template.Files?.ContainsKey("src/CMakeLists.txt") == true))
        {
            throw new StudioXException("PACK_ESPRESSIF_BUILD", "Espressif 包必须使用原生 SDK 组件，不能混用裸机链接脚本、构建参数或探针配置。");
        }
        foreach (var template in device.Templates)
        {
            if (template.EspressifExample is { } example &&
                (profile.Framework != "esp-idf" || template.Files is not null ||
                string.IsNullOrWhiteSpace(example.ExampleDirectory) ||
                !example.ExampleDirectory.StartsWith("templates/", StringComparison.Ordinal) ||
                !template.EntryFile.StartsWith(example.ExampleDirectory.TrimEnd('/') + "/main/", StringComparison.Ordinal)))
            {
                throw new StudioXException("PACK_ESPRESSIF_EXAMPLE", "原生 IDF 示例必须保留包内 main 组件布局，不能混用普通模板文件映射。");
            }
        }
    }
}
