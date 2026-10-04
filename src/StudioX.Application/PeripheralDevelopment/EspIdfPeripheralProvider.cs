namespace StudioX.Application.PeripheralDevelopment;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal sealed partial class EspIdfPeripheralProvider(ToolsetCatalog tools) : IPeripheralDevelopmentProvider
{
    private sealed record Metadata(int FormatVersion, string Id, string Version, string Host, string CompilerId,
        string? Purpose, Dictionary<string, string>? ComponentVersions, Dictionary<string, string>? ResourceDirectories);
    public bool Supports(ProjectManifest project) => project.Kind == ProjectKind.Pack && project.Espressif?.Framework == "esp-idf";

    public async Task<PeripheralDevelopmentContext> ReadAsync(string directory, ProjectManifest project, CancellationToken token)
    {
        var settings = project.Espressif!;
        if (settings.SdkVersion is not ("5.5.4" or "6.1.0"))
        {
            throw new StudioXException("PERIPHERAL_SDK_VERSION", "当前外设代码已适配 ESP-IDF 5.5.4 / 6.1.0，所选 SDK 版本尚未验收。");
        }
        PackValidator.Token(settings.Target);
        PackValidator.Token(project.ToolsetId);
        PackValidator.Version(project.ToolsetVersion);
        tools.RequireEnabled(project.ToolsetId, project.ToolsetVersion);
        var root = PathBoundary.Resolve(tools.RootDirectory, project.ToolsetId + "/" + project.ToolsetVersion);
        using var lease = ToolUsageLease.Acquire(root);
        var metadata = await JsonStore.ReadAsync<Metadata>(PathBoundary.Resolve(root, "toolset.json"), token).ConfigureAwait(false);
        if (metadata.FormatVersion != 1 || metadata.Id != project.ToolsetId || metadata.Version != project.ToolsetVersion ||
            metadata.CompilerId != project.CompilerId || metadata.Host != "win-x64" || metadata.Purpose != settings.Framework ||
            (metadata.ComponentVersions?.GetValueOrDefault("esp-idf") ?? metadata.Version) != settings.SdkVersion)
        {
            throw new StudioXException("PERIPHERAL_SDK_IDENTITY", "外设辅助需要与工程锁定身份一致的开发环境组件。");
        }
        var sdk = PathBoundary.Resolve(root, metadata.ResourceDirectories?.GetValueOrDefault("idf")
            ?? throw new StudioXException("PERIPHERAL_SDK_RESOURCE", "开发环境组件未声明 SDK 资源目录。"));
        var version = await ReadEvidenceAsync(sdk, "version.txt", token).ConfigureAwait(false);
        if (version.Trim().TrimStart('v') != settings.SdkVersion)
        {
            throw new StudioXException("PERIPHERAL_SDK_IDENTITY", "SDK 实际版本与工程锁定版本不同。");
        }
        var pack = await JsonStore.ReadAsync<PackManifest>(PathBoundary.Resolve(directory, "device/manifest.json"), token).ConfigureAwait(false);
        var device = pack.Devices.SingleOrDefault(item => item.Id == project.DeviceId);
        if (pack.FormatVersion != 1 || pack.Id != project.PackId || pack.Version != project.PackVersion || device?.Espressif is not { } profile ||
            profile.Framework != settings.Framework || profile.Target != settings.Target || profile.SdkVersion != settings.SdkVersion ||
            device.ToolsetId != project.ToolsetId || device.ToolsetVersion != project.ToolsetVersion || device.CompilerId != project.CompilerId ||
            !device.Templates.Any(template => template.Id == project.TemplateId))
        {
            throw new StudioXException("PERIPHERAL_DEVICE_IDENTITY", "工程、器件包和 SDK 的目标或版本不一致。");
        }
        var caps = await ReadEvidenceAsync(sdk, $"components/soc/{settings.Target}/include/soc/soc_caps.h", token).ConfigureAwait(false);
        var gpioCount = Number(caps, "SOC_GPIO_PIN_COUNT");
        if (gpioCount is < 1 or > 64)
        {
            throw new StudioXException("PERIPHERAL_CAPABILITY", "SDK 未提供可解析的 GPIO 数量，不能生成引脚配置。");
        }
        var evidence = new StringBuilder().Append(version).Append(caps).Append(System.Text.Json.JsonSerializer.Serialize(metadata, JsonStore.Options))
            .Append(System.Text.Json.JsonSerializer.Serialize(pack, JsonStore.Options));
        var options = new List<PeripheralOption>();
        var documentationVersion = settings.SdkVersion == "6.1.0" ? "6.1" : settings.SdkVersion;
        foreach (var recipe in EspIdfPeripheralRecipes.All)
        {
            token.ThrowIfCancellationRequested();
            var available = recipe.Capability is null || Number(caps, recipe.Capability) > 0;
            var reason = available ? "SDK 支持" : "目标芯片未声明此能力";
            foreach (var relative in recipe.EvidenceFiles)
            {
                var path = PathBoundary.Resolve(sdk, relative);
                if (!File.Exists(path))
                {
                    available = false;
                    reason = "SDK 缺少文件：" + relative;
                    evidence.Append(relative).Append("missing");
                    continue;
                }
                var contents = await ReadEvidenceAsync(sdk, relative, token).ConfigureAwait(false);
                evidence.Append(relative).Append(contents);
                if (relative == $"components/{recipe.Component}/include/{recipe.Header}" &&
                    !Regex.IsMatch(contents, @"\b" + recipe.InitializationApi + @"\s*\(", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                {
                    available = false;
                    reason = "SDK 头文件缺少预期 API：" + recipe.InitializationApi;
                }
            }
            var parameters = recipe.Parameters.Select(p => p.Id is "pin" or "tx" or "rx" or "sda" or "scl" or "mosi" or "miso" or "sclk" or "cs"
                ? p with
                {
                    Maximum = gpioCount - 1
                } : p).ToList();
            var count = recipe.Id switch
            {
                "uart" => Number(caps, "SOC_UART_NUM"),
                "i2c" => Number(caps, "SOC_HP_I2C_NUM"),
                "spi" => Number(caps, "SOC_SPI_PERIPH_NUM"),
                _ => 0
            };
            if (recipe.Id is "uart" or "i2c" or "spi")
            {
                if (count <= (recipe.Id == "spi" ? 1 : 0))
                {
                    available = false;
                    reason = "SDK 未声明所需控制器数量";
                }
                parameters = parameters.Select(p => p.Id == "port" ? p with { Maximum = recipe.Id == "spi" ? count : count - 1 } : p).ToList();
            }
            if (recipe.Id == "rmt" && Number(caps, "SOC_RMT_MEM_WORDS_PER_CHANNEL") <= 0)
            {
                available = false;
                reason = "SDK 未声明 RMT 通道内存容量";
            }
            evidence.Append(recipe.Id).Append(available).Append(reason);
            options.Add(new(recipe.Id, recipe.Name, recipe.Header, recipe.Component, available, reason, parameters.AsReadOnly(), recipe.Notes,
                $"https://docs.espressif.com/projects/esp-idf/en/v{documentationVersion}/{settings.Target}/api-reference/{recipe.Documentation}"));
        }
        return new(directory, project, sdk, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence.ToString()))), gpioCount, options.AsReadOnly());
    }

    public PeripheralCodePreview Generate(PeripheralDevelopmentContext context, string optionId, IReadOnlyDictionary<string, string> values)
    {
        var option = context.Options.SingleOrDefault(item => item.Id == optionId)
            ?? throw new StudioXException("PERIPHERAL_OPTION", "外设选项不存在。");
        if (!option.Available)
        {
            throw new StudioXException("PERIPHERAL_UNAVAILABLE", option.Availability);
        }
        if (values.Keys.Any(key => !option.Parameters.Any(p => p.Id == key)))
        {
            throw new StudioXException("PERIPHERAL_PARAMETER", "包含未知外设参数。");
        }
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in option.Parameters)
        {
            var value = values.GetValueOrDefault(parameter.Id, parameter.DefaultValue).Trim();
            if (parameter.Id == "name")
            {
                // 统一前缀隔离用户名称；只接受短 ASCII 标识符，避免注入 C 代码或保留标识符。
                if (!NamePattern().IsMatch(value))
                {
                    throw new StudioXException("PERIPHERAL_PARAMETER", "名称须为字母开头的 1–32 位字母、数字或下划线。");
                }
                normalized[parameter.Id] = "sx_" + value;
            }
            else
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < parameter.Minimum || number > parameter.Maximum)
                {
                    throw new StudioXException("PERIPHERAL_PARAMETER", $"{parameter.Label} 须为 {parameter.Minimum}–{parameter.Maximum} 范围的整数。");
                }
                normalized[parameter.Id] = number.ToString(CultureInfo.InvariantCulture);
            }
        }
        var pins = normalized.Where(p => p.Key is "pin" or "tx" or "rx" or "sda" or "scl" or "mosi" or "miso" or "sclk" or "cs").Select(p => p.Value).ToArray();
        if (pins.Distinct(StringComparer.Ordinal).Count() != pins.Length)
        {
            throw new StudioXException("PERIPHERAL_PIN_CONFLICT", "同一外设的引脚不能重复。");
        }
        var code = EspIdfPeripheralCode.Generate(optionId, normalized);
        var dependencies = new[] { option.Component, "esp_driver_gpio" }.Distinct(StringComparer.Ordinal).ToArray();
        return new(normalized["name"], code, $"组件依赖：{string.Join(' ', dependencies)}。添加到工程时自动补齐，已有依赖保留；头文件路径为 {option.Header}。",
            $"{context.Project.DeviceId} / {context.Project.Espressif!.Target} · ESP-IDF {context.Project.Espressif.SdkVersion}\n" + option.Notes +
            "\n在文件顶层插入完整代码，再由任务调用 sx_名称_init() 并检查 esp_err_t；结束后调用 deinit()。同一实例由一个任务拥有，初始化/释放不能并发。请确认板级 Flash、PSRAM、USB、控制台和启动绑带占用；SDK 支持不代表板上可用。多个模板之间的引脚、控制器和定时器资源须自行分配。", dependencies);
    }

    private static async Task<string> ReadEvidenceAsync(string root, string relative, CancellationToken token)
    {
        var path = PathBoundary.Resolve(root, relative);
        if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024)
        {
            throw new StudioXException("PERIPHERAL_SDK_FILE", "SDK 文件缺失或超出读取限制：" + relative);
        }
        return await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
    }
    private static int Number(string text, string macro)
    {
        var match = Regex.Match(text, @"(?m)^\s*#define\s+" + Regex.Escape(macro) + @"\s+\(?\s*(\d+)[uUlL]*\s*\)?(?:\s|$)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : 0;
    }
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,31}$", RegexOptions.CultureInvariant)] private static partial Regex NamePattern();
}
