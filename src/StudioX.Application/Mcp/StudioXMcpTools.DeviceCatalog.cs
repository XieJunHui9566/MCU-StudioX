namespace StudioX.Application.Mcp;

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using StudioX.Packages;

/// <summary>只从已安装的 StudioX 格式 1 器件包读取可核实的型号资料；不根据名称推断容量或下载配置。</summary>
public sealed partial class StudioXMcpTools
{
    private const int MaximumDeviceSearchResults = 40;

    [McpServerTool(Name = "device_search")]
    [Description("检索本机已安装器件包的准确型号；返回包 ID、版本和清单完整性信息，不访问网络。搜索结果仅核验目录与清单，使用前以 device_info 完整核验器件包。")]
    public async Task<string> DeviceSearchAsync(
        [Description("型号、厂商或器件包关键词；留空列出前几项。")]
        string query = "",
        [Description("最多返回的型号数，范围 1–40，默认 20。")]
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > MaximumDeviceSearchResults)
            throw new ArgumentOutOfRangeException(nameof(limit), "范围为 1–40。");
        query = (query ?? "").Trim();
        if (query.Length > 128)
            throw new ArgumentOutOfRangeException(nameof(query), "关键词不能超过 128 字符。");

        var catalog = await Services.Packs.ListCatalogAsync(cancellationToken).ConfigureAwait(false);
        var found = catalog.SelectMany(pack => pack.Manifest.Devices.OfType<DeviceDefinition>()
                .Where(device => !string.IsNullOrWhiteSpace(device.Id))
                .Select(device => new { Pack = pack, Device = device }))
            .Where(item => query.Length == 0 ||
                item.Device.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (item.Device.DisplayName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                item.Pack.Manifest.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Pack.Manifest.Vendor.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Device.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Pack.Manifest.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Pack.Manifest.Version, StringComparer.Ordinal)
            .ToArray();
        return JsonSerializer.Serialize(new
        {
            query,
            total = found.Length,
            returned = Math.Min(limit, found.Length),
            devices = found.Take(limit).Select(item => new
            {
                id = item.Device.Id,
                name = item.Device.DisplayName,
                architecture = item.Device.Architecture,
                vendor = item.Pack.Manifest.Vendor,
                packId = item.Pack.Manifest.Id,
                packVersion = item.Pack.Manifest.Version,
                packContentHash = item.Pack.ContentHash
            }).ToArray(),
            source = "installed-studiox-pack-format-1",
            verification = "installation-index-and-manifest-sha256; use device_info for full-pack verification"
        });
    }

    [McpServerTool(Name = "device_info")]
    [Description("按准确型号 ID 查询已安装器件包的架构、Flash/RAM、编译参数和可用探针；先完整核验包内全部文件。若多个包同型号则要求明确包 ID/版本。")]
    public async Task<string> DeviceInfoAsync(
        [Description("准确型号 ID，例如 STM32F407ZGT6；不接受模糊型号来推断容量。")]
        string deviceId,
        [Description("可选的准确器件包 ID；重名时必须指定。")]
        string? packId = null,
        [Description("可选的准确器件包版本；重名时必须指定。")]
        string? packVersion = null,
        CancellationToken cancellationToken = default)
    {
        var lookup = await LocateDeviceAsync(deviceId, packId, packVersion, cancellationToken).ConfigureAwait(false);
        if (lookup.Error is not null) return lookup.Error;
        var pack = await PackRepository.VerifyAsync(lookup.Pack!, cancellationToken).ConfigureAwait(false);
        var device = pack.Manifest.Devices.Single(item => item.Id.Equals(deviceId.Trim(), StringComparison.OrdinalIgnoreCase));
        return JsonSerializer.Serialize(new
        {
            id = device.Id,
            name = device.DisplayName,
            architecture = device.Architecture,
            memory = new
            {
                flashOrigin = $"0x{device.FlashOrigin:X8}",
                flashBytes = device.FlashBytes,
                ramOrigin = $"0x{device.RamOrigin:X8}",
                ramBytes = device.RamBytes,
                applicationFlashBytes = device.OpenOcd?.ApplicationFlashBytes
            },
            toolset = new { id = device.ToolsetId, version = device.ToolsetVersion, compiler = device.CompilerId },
            cpuFlags = device.CpuFlags.Take(60).ToArray(),
            omittedCpuFlags = Math.Max(0, device.CpuFlags.Count - 60),
            defines = device.Defines.Take(60).ToArray(),
            omittedDefines = Math.Max(0, device.Defines.Count - 60),
            templates = device.Templates.Select(item => new { item.Id, item.DisplayName }).ToArray(),
            probes = device.OpenOcd?.Probes.Select(item => new
            {
                item.Id, item.DisplayName, item.Transport, item.DefaultSpeedKhz
            }).ToArray() ?? [],
            source = PackSource(pack, "full-sha256-and-format-validation")
        });
    }

    [McpServerTool(Name = "device_templates")]
    [Description("按准确型号 ID 列出已安装器件包明确声明的工程模板；先完整核验包内全部文件。不生成或编译工程。")]
    public async Task<string> DeviceTemplatesAsync(
        [Description("准确型号 ID。")]
        string deviceId,
        [Description("可选的准确器件包 ID；重名时必须指定。")]
        string? packId = null,
        [Description("可选的准确器件包版本；重名时必须指定。")]
        string? packVersion = null,
        CancellationToken cancellationToken = default)
    {
        var lookup = await LocateDeviceAsync(deviceId, packId, packVersion, cancellationToken).ConfigureAwait(false);
        if (lookup.Error is not null) return lookup.Error;
        var pack = await PackRepository.VerifyAsync(lookup.Pack!, cancellationToken).ConfigureAwait(false);
        var device = pack.Manifest.Devices.Single(item => item.Id.Equals(deviceId.Trim(), StringComparison.OrdinalIgnoreCase));
        return JsonSerializer.Serialize(new
        {
            deviceId = device.Id,
            templates = device.Templates.Take(40).Select(item => new
            {
                id = item.Id,
                name = item.DisplayName,
                description = LimitOutput(item.Description ?? "", 400),
                entryFile = item.EntryFile,
                additionalSourceCount = item.Build?.Sources.Count ?? 0
            }).ToArray(),
            omittedTemplates = Math.Max(0, device.Templates.Count - 40),
            source = PackSource(pack, "full-sha256-and-format-validation")
        });
    }

    private async Task<(InstalledPack? Pack, string? Error)> LocateDeviceAsync(
        string deviceId, string? packId, string? packVersion, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 128)
            throw new ArgumentException("必须提供不超过 128 字符的准确型号 ID。", nameof(deviceId));
        if (packId?.Length > 128 || packVersion?.Length > 128)
            throw new ArgumentException("器件包 ID 或版本过长。");

        var exactId = deviceId.Trim();
        var catalog = await Services.Packs.ListCatalogAsync(cancellationToken).ConfigureAwait(false);
        var matches = catalog.Where(pack =>
                (packId is null || pack.Manifest.Id.Equals(packId, StringComparison.OrdinalIgnoreCase)) &&
                (packVersion is null || pack.Manifest.Version.Equals(packVersion, StringComparison.Ordinal)) &&
                pack.Manifest.Devices.Any(device => device.Id.Equals(exactId, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (matches.Length == 1) return (matches[0], null);
        return (null, JsonSerializer.Serialize(new
        {
            status = matches.Length == 0 ? "not_found" : "ambiguous",
            deviceId = exactId,
            message = matches.Length == 0
                ? "已安装器件包中没有此准确型号。请检查型号或安装对应器件包。"
                : "多个已安装器件包含相同型号，请指定 packId 和 packVersion。",
            candidates = matches.Take(40).Select(pack => new
            {
                packId = pack.Manifest.Id,
                packVersion = pack.Manifest.Version,
                packContentHash = pack.ContentHash
            }).ToArray(),
            omittedCandidates = Math.Max(0, matches.Length - 40),
            source = "installed-studiox-pack-format-1"
        }));
    }

    private static object PackSource(InstalledPack pack, string verification) => new
    {
        kind = "installed-studiox-pack-format-1",
        packId = pack.Manifest.Id,
        packVersion = pack.Manifest.Version,
        vendor = pack.Manifest.Vendor,
        packContentHash = pack.ContentHash,
        manifest = "manifest.json",
        verification
    };
}
