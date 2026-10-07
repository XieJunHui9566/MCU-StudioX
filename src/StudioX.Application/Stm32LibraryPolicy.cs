namespace StudioX.Application;

using System.Text.Json;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>公开下载的 STM32 包必须声明厂商库矩阵；每个器件提供裸机及 RTOS 组合。</summary>
internal static class Stm32LibraryPolicy
{
    public static void Validate(PackManifest manifest, JsonElement json, IReadOnlySet<string> files)
    {
        var stm32 = manifest.Devices.Where(d => d.Id.StartsWith("STM32", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (stm32.Length == 0)
        {
            return;
        }
        void Require(bool valid, string message)
        {
            if (!valid)
            {
                throw new StudioXException("PACK_STM32_LIBRARIES", "STM32 器件包外设库不完整：" + message);
            }
        }
        Require(json.TryGetProperty("stm32LibrarySupport", out var support) &&
            support.TryGetProperty("policyVersion", out var version) && version.GetInt32() == 1 &&
            support.TryGetProperty("libraries", out var declared) && declared.ValueKind == JsonValueKind.Array,
            "缺少外设库矩阵，不能从公开目录安装或自动更新此包。");
        var libraries = support.GetProperty("libraries").EnumerateArray().Select(v => v.GetString() ?? "").ToHashSet(StringComparer.Ordinal);
        Require(libraries.Contains("hal") && libraries.All(v => v is "hal" or "spl" or "ll"), "库矩阵必须包含 HAL，且只接受 HAL/SPL/LL。");
        Require(support.TryGetProperty("evidence", out var evidence) && files.Contains(evidence.GetString() ?? ""), "缺少 SDK 来源与版本证据。");
        foreach (var device in stm32)
        {
            // SPL 的历史支持系列来自 ST 标准外设库目录；新系列不得套用旧 SPL。
            var legacy = new[] { "STM32F0", "STM32F1", "STM32F2", "STM32F3", "STM32F4", "STM32L1" }.Any(p => device.Id.StartsWith(p, StringComparison.Ordinal));
            Require(libraries.Contains("spl") == legacy, device.Id + " 的 SPL 支持与厂商系列不一致。");
            if (device.Id.StartsWith("STM32F1", StringComparison.Ordinal) || device.Id.StartsWith("STM32F4", StringComparison.Ordinal))
            {
                Require(libraries.Contains("ll"), device.Id + " 缺少 STM32Cube LL。");
            }
            var expected = libraries.SelectMany(l => new[] { l, l + "-freertos" }).ToHashSet(StringComparer.Ordinal);
            Require(device.Templates.Select(t => t.Id).ToHashSet(StringComparer.Ordinal).SetEquals(expected), device.Id + " 的裸机/RTOS 模板不齐全或存在旧模板。");
            foreach (var template in device.Templates)
            {
                var library = template.Id.Split('-')[0];
                var macro = library switch
                {
                    "hal" => "USE_HAL_DRIVER",
                    "spl" => "USE_STDPERIPH_DRIVER",
                    _ => "USE_FULL_LL_DRIVER"
                };
                var build = TemplateResolver.Resolve(device, template.Id);
                Require(build.Defines.Contains(macro), device.Id + "/" + template.Id + " 缺少库选择宏。");
                Require(build.Sources.Any(p => p.StartsWith("sdk/" + library + "/", StringComparison.Ordinal) && files.Contains(p)), device.Id + "/" + template.Id + " 缺少对应外设库源码。");
                Require(build.IncludeDirectories.Any(p => p.StartsWith("sdk/" + library + "/", StringComparison.Ordinal) && files.Any(f => f.StartsWith(p.TrimEnd('/') + "/", StringComparison.Ordinal) && f.EndsWith(".h", StringComparison.Ordinal))), device.Id + "/" + template.Id + " 缺少库头文件。");
                if (template.Id.EndsWith("-freertos", StringComparison.Ordinal))
                {
                    Require(build.Sources.Any(p => p.StartsWith("sdk/freertos/", StringComparison.Ordinal) && p.EndsWith("tasks.c", StringComparison.Ordinal) && files.Contains(p)), device.Id + "/" + template.Id + " 缺少 FreeRTOS 内核。");
                }
            }
        }
    }
}
