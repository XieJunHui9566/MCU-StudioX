namespace StudioX.Engine;

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Foundation;

/// <summary>只读解析 SDK 生成的下载布局；不执行 JSON 中的任意参数或打开串口。</summary>
public static class EspressifFlashLayoutReader
{
    public static async Task<EspressifFlashLayout> ParseAsync(string projectRoot, string flashArgsRelativePath,
        string expectedTarget, ulong declaredPhysicalFlashBytes = 0, CancellationToken token = default)
    {
        var root = Path.GetFullPath(projectRoot);
        if (!flashArgsRelativePath.StartsWith(".build/", StringComparison.Ordinal))
        {
            throw Invalid("下载布局必须位于当前工程的 .build 目录。");
        }
        var path = PathBoundary.Resolve(root, flashArgsRelativePath);
        if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 1024 * 1024)
        {
            throw Invalid("缺少有效的 flasher_args.json；请先成功编译。");
        }
        var bytes = await File.ReadAllBytesAsync(path, token);
        using var document = JsonDocument.Parse(bytes);
        EnsureUniqueProperties(document.RootElement);
        var json = document.RootElement;
        var extra = json.GetProperty("extra_esptool_args");
        // ESP8266 RTOS SDK 3.4 的官方布局没有 chip/stub 字段；构建后端另外核对 CMakeCache 的 esp8266 目标。
        var target = extra.TryGetProperty("chip", out var chip) ? chip.GetString() : expectedTarget == "esp8266" ? "esp8266" : null;
        if (target != expectedTarget || target is not ("esp32" or "esp32p4" or "esp32s3" or "esp32c3" or "esp32c5" or "esp32c6" or "esp8266"))
        {
            throw Invalid("构建布局的芯片与工程目标不一致。");
        }
        var settings = json.GetProperty("flash_settings");
        var mode = RequiredString(settings, "flash_mode");
        var frequency = RequiredString(settings, "flash_freq");
        var size = RequiredString(settings, "flash_size");
        if (mode is not ("qio" or "qout" or "dio" or "dout") ||
            frequency is not ("120m" or "80m" or "60m" or "48m" or "40m" or "30m" or "26m" or "24m" or "20m" or "16m" or "15m" or "12m"))
        {
            throw Invalid("SDK 下载布局包含未支持的 Flash 模式或频率。");
        }
        var capacity = FlashCapacity(size);
        if (declaredPhysicalFlashBytes > 0 && capacity > declaredPhysicalFlashBytes)
        {
            throw Invalid("SDK 配置的 Flash 容量超过器件包核实的物理容量。");
        }
        var writeArguments = json.GetProperty("write_flash_args").EnumerateArray().Select(value => value.GetString()).ToArray();
        string?[] expected = ["--flash_mode", mode, "--flash_size", size, "--flash_freq", frequency];
        if (writeArguments.Length != expected.Length || writeArguments.Select(value => value?.Replace('-', '_')).
            Where((value, index) => value != expected[index]?.Replace('-', '_')).Any())
        {
            throw Invalid("下载布局包含额外写入参数；加密、强制写入及整片擦除不在本入口的支持范围。");
        }
        if (extra.TryGetProperty("before", out var before) && before.GetString() is not ("default_reset" or "default-reset") ||
            extra.TryGetProperty("after", out var after) && after.GetString() is not ("hard_reset" or "hard-reset"))
        {
            throw Invalid("当前入口只支持普通固件的复位下载；请勿使用安全启动或加密构建。");
        }
        var useStub = extra.TryGetProperty("stub", out var stub) ? stub.GetBoolean() : expectedTarget == "esp8266";
        if (expectedTarget != "esp8266" && !extra.TryGetProperty("stub", out _))
        {
            throw Invalid("ESP-IDF 下载布局缺少明确的 stub 配置。");
        }
        var buildDirectory = Path.GetDirectoryName(path)!;
        await EspressifFlashSecurity.ValidateAsync(root, buildDirectory, token);
        var images = new List<EspressifFlashImage>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in json.GetProperty("flash_files").EnumerateObject())
        {
            token.ThrowIfCancellationRequested();
            if (!TryOffset(item.Name, out var offset))
            {
                throw Invalid("下载布局包含无效的 Flash 偏移。");
            }
            var file = item.Value.GetString() ?? throw Invalid("下载映像路径为空。");
            var absolute = PathBoundary.Resolve(buildDirectory, file.Replace('\\', '/'));
            var relative = Path.GetRelativePath(root, absolute).Replace('\\', '/');
            if (!relative.StartsWith(".build/", StringComparison.Ordinal) || !seenPaths.Add(relative) ||
                !Path.GetExtension(file).Equals(".bin", StringComparison.OrdinalIgnoreCase) || !File.Exists(absolute))
            {
                throw Invalid("下载映像必须是不重复的工程构建 BIN 文件。");
            }
            var length = new FileInfo(absolute).Length;
            if (length <= 0 || (ulong)offset + (ulong)length > capacity)
            {
                throw Invalid("下载映像为空或超出已配置的 Flash 容量。");
            }
            await using var stream = File.OpenRead(absolute);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
            images.Add(new(offset, relative, hash, length));
        }
        if (images.Count == 0)
        {
            throw Invalid("下载布局没有映像。");
        }
        var ordered = images.OrderBy(image => image.Offset).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            // esptool 按 4 KiB 扇区擦写；相邻映像不能共享一个擦除扇区。
            var previous = ordered[index - 1];
            var eraseEnd = ((ulong)previous.Offset + (ulong)previous.Bytes + 4095) & ~4095UL;
            if (((ulong)ordered[index].Offset & ~4095UL) < eraseEnd)
            {
                throw Invalid("下载映像重叠或共享擦除扇区。");
            }
        }
        foreach (var item in json.EnumerateObject())
        {
            if (item.Value.ValueKind == JsonValueKind.Object && item.Value.TryGetProperty("encrypted", out var encrypted) &&
                encrypted.ValueKind != JsonValueKind.False &&
                !(encrypted.ValueKind == JsonValueKind.String && encrypted.GetString() == "false"))
            {
                throw Invalid("当前下载入口不支持加密映像。");
            }
        }
        return new(flashArgsRelativePath, Convert.ToHexString(SHA256.HashData(bytes)), target,
            mode, frequency, size, useStub, ordered);
    }

    public static ulong FlashCapacity(string size) => size switch
    {
        "256KB" => 256UL * 1024,
        "512KB" => 512UL * 1024,
        "1MB" => 1UL * 1024 * 1024,
        "2MB" => 2UL * 1024 * 1024,
        "4MB" => 4UL * 1024 * 1024,
        "8MB" => 8UL * 1024 * 1024,
        "16MB" => 16UL * 1024 * 1024,
        "32MB" => 32UL * 1024 * 1024,
        "64MB" => 64UL * 1024 * 1024,
        "128MB" => 128UL * 1024 * 1024,
        _ => throw Invalid("Flash 容量必须由 SDK 构建明确指定，不允许 detect 或 keep。")
    };

    private static string RequiredString(JsonElement value, string property) =>
        value.GetProperty(property).GetString() ?? throw Invalid($"下载布局缺少 {property}。");

    private static bool TryOffset(string text, out uint offset) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out offset)
        : uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out offset);

    private static void EnsureUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject())
            {
                if (!names.Add(item.Name))
                {
                    throw Invalid("下载布局包含重复字段。");
                }
                EnsureUniqueProperties(item.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                EnsureUniqueProperties(item);
            }
        }
    }

    private static StudioXException Invalid(string message) => new("ESP_FLASH_LAYOUT", message);
}
