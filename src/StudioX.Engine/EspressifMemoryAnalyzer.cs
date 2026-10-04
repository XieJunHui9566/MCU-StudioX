namespace StudioX.Engine;

using System.Text.Json;
using StudioX.Foundation;

/// <summary>沿用 SDK 对共享 IRAM/DRAM 的归并规则，不将链接地址窗口当作物理芯片容量。</summary>
internal static class EspressifMemoryAnalyzer
{
    internal static async Task<BuildMemoryRegion[]> AnalyzeAsync(string root, string map, ResolvedToolset tools,
        EspressifProjectSettings sdk, CancellationToken token)
    {
        var environment = await EspressifBuildEnvironment.CreateAsync(root, tools, sdk, token);
        var nativeMap = EspressifNativePath.For(map);
        var arguments = sdk.Framework == "esp-idf"
            ? new[] { "-m", "esp_idf_size.ng", "--format", "json2", nativeMap }
            : new[] { PathBoundary.Resolve(environment["IDF_PATH"], "tools/idf_size.py"), "--json", nativeMap };
        var result = await new ProcessRunner().RunAsync(new(EspressifNativePath.For(tools.Tool("python")), arguments,
            EspressifNativePath.For(root), TimeSpan.FromMinutes(2), environment,
            RemoveEnvironment: ToolsetEnvironment.AmbientVariables), token);
        // 厂商警告与 JSON 一并保留，即使解析失败也能检查原始工具输出。
        await File.WriteAllTextAsync(PathBoundary.Resolve(root, ".build/studiox-idf-size.log"),
            $"exit={result.ExitCode}; timeout={result.TimedOut}; truncated={result.OutputTruncated}\n" +
            result.StandardOutput + "\n[stderr]\n" + result.StandardError, token);
        if (!result.Success || result.OutputTruncated)
        {
            throw new StudioXException("ESPRESSIF_SIZE", "SDK 内存统计失败：" + result.StandardError + "\n" + result.StandardOutput);
        }
        try
        {
            return Parse(result.StandardOutput, sdk);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("SDK 内存统计格式无效；原始输出保留在 .build/studiox-idf-size.log。", exception);
        }
    }

    private static BuildMemoryRegion[] Parse(string json, EspressifProjectSettings sdk)
    {
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement;
        if (sdk.Framework == "esp8266-rtos-sdk")
        {
            BuildMemoryRegion Ram(string name, string usedName, string availableName)
            {
                var used = value.GetProperty(usedName).GetUInt64();
                var total = checked((long)used + value.GetProperty(availableName).GetInt64());
                if (total < 0)
                {
                    throw new InvalidDataException("ESP8266 SDK 统计返回了负容量。");
                }
                return new(name, 0, (ulong)total, used, IsLogical: true);
            }
            return [Ram("DRAM", "used_dram", "available_dram"), Ram("IRAM", "used_iram", "available_iram"),
                new("Flash Code", 0, 0, value.GetProperty("flash_code").GetUInt64(), IsLogical: true),
                new("Flash Data", 0, 0, value.GetProperty("flash_rodata").GetUInt64(), IsLogical: true)];
        }
        if (value.GetProperty("version").GetString() != "1.1")
        {
            throw new InvalidDataException("不支持此 SDK 内存统计 JSON 格式。");
        }
        var regions = new List<BuildMemoryRegion>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var region in value.GetProperty("layout").EnumerateArray())
        {
            var name = region.GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
            {
                throw new InvalidDataException("SDK 内存统计缺少唯一的存储区名称。");
            }
            // Flash 总容量为零是厂商明确的未知值；不能改用映射窗口或假定板载 Flash。
            regions.Add(new(name, 0, region.GetProperty("total").GetUInt64(), region.GetProperty("used").GetUInt64(), IsLogical: true));
        }
        if (regions.Count == 0)
        {
            throw new InvalidDataException("SDK 内存统计没有可用的存储区。");
        }
        return regions.ToArray();
    }
}
