namespace StudioX.Packages;

using System.Text.Json;
using StudioX.Foundation;

/// <summary>PIC 程序地址按指令字组织，RAM 分银行；不能用连续字节区间推导下载地址。</summary>
public sealed record PicDeviceDefinition(string CompilerTarget, int ProgramWords, int ProgramWordBits, int EepromBytes, string DfpVersion)
{
    public const string ToolsetId = "microchip.xc8";
    public const string ToolsetVersion = "4.0.0";
    public const string CompilerId = "xc8-4.00-pic";
    private static readonly IReadOnlyDictionary<string, PicCatalogEntry> Catalog = ReadCatalog();

    public string CapacityDescription => $"{ProgramWords} 个 {ProgramWordBits} 位程序字 / {Catalog["PIC" + CompilerTarget].RamBytes} B 分银行 RAM / {EepromBytes} B EEPROM";

    private static IReadOnlyDictionary<string, PicCatalogEntry> ReadCatalog()
    {
        using var stream = typeof(PicDeviceDefinition).Assembly.GetManifestResourceStream("StudioX.Packages.Pic16Catalog.json")!;
        return JsonSerializer.Deserialize<PicCatalogEntry[]>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
    }

    public static void Validate(DeviceDefinition device)
    {
        if (!Catalog.TryGetValue(device.Id, out var entry) || device.Architecture != "pic" + entry.ProgramWordBits ||
            device.Pic != new PicDeviceDefinition(device.Id[3..], entry.ProgramWords, entry.ProgramWordBits, entry.EepromBytes, "1.9.176") ||
            device.ToolsetId != ToolsetId || device.ToolsetVersion != ToolsetVersion || device.CompilerId != CompilerId ||
            device.FlashOrigin != 0 || device.FlashBytes != entry.ProgramWords * entry.ProgramWordBits / 8 || device.RamOrigin != 0 || device.RamBytes != entry.RamBytes ||
            device.OpenOcd is not null || device.Espressif is not null || device.LinkerScript != "" ||
            device.CpuFlags is not { Count: 0 } || device.CompileOptions is not { Count: 0 } || device.LinkOptions is not { Count: 0 } ||
            device.Sources is not { Count: 0 } || device.IncludeDirectories is not { Count: 0 } || device.Defines is not { Count: 0 } ||
            device.Templates is not { Count: > 0 } ||
            device.Templates.Any(template => template.MicroPython is not null || template.Build is not null || template.EspressifExample is not null))
        {
            throw new StudioXException("PACK_PIC", "PIC 型号、容量或工具身份不在已核验的 PIC16Fxxx DFP 1.9.176 目录中；不使用 GCC 链接脚本或 OpenOCD。");
        }
    }
}
