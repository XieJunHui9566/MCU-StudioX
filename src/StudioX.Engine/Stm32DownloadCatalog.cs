namespace StudioX.Engine;

using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>由已验证的 F1/F4 器件包生成；CubeMX 工程无需安装包含 SDK 的器件包。</summary>
internal static partial class Stm32DownloadCatalog
{
    internal sealed record Profile(string Id, string Architecture, uint FlashOrigin, uint FlashBytes,
        uint RamOrigin, uint RamBytes, string TargetScript);
    private sealed record Catalog(Profile[] Devices);
    private static readonly Lazy<Catalog> Data = new(() =>
    {
        using var stream = typeof(Stm32DownloadCatalog).Assembly.GetManifestResourceStream("StudioX.Engine.Resources.stm32-download.json")!;
        return JsonSerializer.Deserialize<Catalog>(stream, JsonStore.Options)!;
    });

    // 只去除明确的封装/温度/卷带后缀；含 (E-G) 等容量歧义的 Mcu.Name 不自动匹配。
    [GeneratedRegex(@"^(STM32F[14][0-9]{2}[A-Z][0-9A-Z])(?:[A-Z][0-9](?:TR)?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PartNumber();

    internal static Profile? FindProfile(string deviceId)
    {
        var match = PartNumber().Match(deviceId.ToUpperInvariant());
        if (!match.Success) return null;
        return Data.Value.Devices.SingleOrDefault(item => item.Id == match.Groups[1].Value);
    }

    public static DownloadConfiguration? Find(ProjectManifest project)
    {
        var profile = FindProfile(project.DeviceId);
        if (profile is null) return null;
        var definition = new OpenOcdDefinition("", [
            new("stlink", "ST-Link", "interface/stlink.cfg", "swd", 2000),
            new("cmsis-dap", "DAP-Link (CMSIS-DAP)", "interface/cmsis-dap.cfg", "swd", 2000),
            new("jlink", "J-Link", "interface/jlink.cfg", "swd", 2000)]);
        var device = new DeviceDefinition(project.DeviceId, project.DeviceId, profile.Architecture,
            profile.FlashOrigin, profile.FlashBytes, profile.RamOrigin, profile.RamBytes,
            CubeMxImportService.ToolsetId, CubeMxImportService.ToolsetVersion, CubeMxImportService.CompilerId,
            [], [], [], [], "", [], [], [], definition);
        return new(device, definition, new("stlink", 2000), profile.TargetScript);
    }
}
