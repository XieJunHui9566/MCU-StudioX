namespace StudioX.Engine;

using System.Globalization;
using System.Text.RegularExpressions;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>准确 STC 8 位型号与模板声明的内部 RAM / XDATA 预算，包含模板保留区。</summary>
public sealed record StcRamLimits(int IdataSizeBytes, int XdataOrigin, int XdataSizeBytes)
{
    public int MaximumXdataBytes(bool mon51Profile = false) => mon51Profile
        ? Math.Min(XdataSizeBytes, Math.Max(0, 1024 - XdataOrigin)) : XdataSizeBytes;

    public void Validate(ProjectBuildSettings settings) => Validate(settings.IdataSizeBytes, settings.XdataSizeBytes, settings.Mon51Profile);

    public void Validate(int? idataSizeBytes, int? xdataSizeBytes, bool mon51Profile = false)
    {
        if (mon51Profile && XdataOrigin is < 0 or >= 1024)
        {
            throw new StudioXException("BUILD_SETTINGS", "Mon51 用户 XDATA 起始地址必须位于 0000..03FF。");
        }
        if (idataSizeBytes is { } idata && (idata < 1 || idata > IdataSizeBytes))
        {
            throw new StudioXException("BUILD_SETTINGS", $"内部 RAM 上限须为 1–{IdataSizeBytes} 字节；DATA、IDATA 与栈共享此空间。");
        }
        var maximum = MaximumXdataBytes(mon51Profile);
        if (maximum < 0 || xdataSizeBytes is { } xdata && (xdata < 0 || xdata > maximum))
        {
            throw new StudioXException("BUILD_SETTINGS", $"XDATA 上限须为 0–{maximum} 字节，起始地址 {XdataOrigin} 保持模板设置；0 不允许分配 XDATA。");
        }
    }

    internal int EffectiveXdataSize(ProjectBuildSettings settings) => settings.XdataSizeBytes ?? MaximumXdataBytes(settings.Mon51Profile);

    public static async Task<StcRamLimits?> ReadAsync(string root, ProjectManifest project, CancellationToken token = default)
    {
        if (project.ToolsetId != "stc.sdcc")
        {
            return null;
        }
        var pack = await JsonStore.ReadAsync<PackManifest>(PathBoundary.Resolve(root, "device/manifest.json"), token);
        if (pack.FormatVersion != 1 || pack.Id != project.PackId || pack.Version != project.PackVersion)
        {
            throw new StudioXException("BUILD_SETTINGS", "工程中的 STC 器件包清单与工程身份不匹配。");
        }
        var device = pack.Devices.SingleOrDefault(device => device.Id == project.DeviceId);
        if (device is null || device.ToolsetId != project.ToolsetId || device.Architecture != "mcs51" ||
            !device.Templates.Any(template => template.Id == project.TemplateId))
        {
            throw new StudioXException("BUILD_SETTINGS", "STC 器件包缺少准确型号和模板的 RAM 配置。");
        }
        var options = TemplateResolver.Resolve(device, project.TemplateId).LinkOptions;
        int Read(string option)
        {
            var indices = Enumerable.Range(0, options.Count).Where(index => options[index] == option).ToArray();
            if (indices.Length != 1 || indices[0] + 1 >= options.Count || !TryNumber(options[indices[0] + 1], out var value))
            {
                throw new StudioXException("BUILD_SETTINGS", "STC 模板缺少唯一有效的 " + option);
            }
            return value;
        }
        var iram = Read("--iram-size");
        var origin = Read("--xram-loc");
        var xram = Read("--xram-size");
        if (iram is < 1 or > 256 || origin < 0 || xram < 0 || (long)origin + xram > 65536 ||
            (long)iram + origin + xram > device.RamBytes)
        {
            throw new StudioXException("BUILD_SETTINGS", "STC 模板的内部 RAM / XDATA 参数超过已声明的准确型号容量。");
        }
        return new(iram, origin, xram);
    }

    internal void VerifyLinkArguments(string[] arguments, ProjectBuildSettings settings)
    {
        foreach (var (option, expected) in new[]
        {
            ("--iram-size", settings.IdataSizeBytes ?? IdataSizeBytes),
            ("--xram-loc", XdataOrigin),
            ("--xram-size", EffectiveXdataSize(settings))
        })
        {
            var indices = Enumerable.Range(0, arguments.Length).Where(index => arguments[index] == option).ToArray();
            if (indices.Length != 1 || indices[0] + 1 == arguments.Length ||
                !TryNumber(arguments[indices[0] + 1], out var value) || value != expected)
            {
                throw new StudioXException("BUILD_SETTINGS_NOT_APPLIED", $"SDCC 最终链接命令必须唯一应用 {option} {expected}；请移除自定义 CMake 冲突选项。");
            }
        }
    }

    internal async Task VerifyBuildAsync(string build, ProjectBuildSettings settings, CancellationToken token)
    {
        var mem = await File.ReadAllTextAsync(Path.Combine(build, "firmware.mem"), token);
        var stack = Regex.Match(mem, @"Stack starts at:\s*0x(?<start>[0-9A-Fa-f]+).*?with\s+(?<available>\d+)\s+bytes available", RegexOptions.CultureInvariant);
        var iram = settings.IdataSizeBytes ?? IdataSizeBytes;
        if (!stack.Success || !int.TryParse(stack.Groups["start"].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var start) ||
            !int.TryParse(stack.Groups["available"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var available) || start + available != iram)
        {
            throw new StudioXException("BUILD_SETTINGS_NOT_APPLIED", $"SDCC .mem 的内部 RAM / 栈边界与 {iram} 字节设置不符。");
        }
        var xram = EffectiveXdataSize(settings);
        foreach (var name in new[] { "EXTERNAL RAM", "PAGED EXT. RAM" })
        {
            var line = mem.Split('\n').SingleOrDefault(line => line.TrimStart().StartsWith(name + " ", StringComparison.Ordinal));
            var match = line is null ? Match.Empty : Regex.Match(line, @"(?:(?:0x(?<start>[0-9A-Fa-f]+)\s+0x(?<end>[0-9A-Fa-f]+))\s+)?(?<used>\d+)\s+(?<max>\d+)\s*$", RegexOptions.CultureInvariant);
            if (!match.Success || !TryNumber(match.Groups["used"].Value, out var used) || !TryNumber(match.Groups["max"].Value, out var maximum) ||
                name == "EXTERNAL RAM" && maximum != xram)
            {
                throw new StudioXException("BUILD_SETTINGS_NOT_APPLIED", "SDCC .mem 的 XDATA 容量与所选设置不符。");
            }
            if (used > 0 && (!match.Groups["start"].Success ||
                !int.TryParse(match.Groups["start"].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var first) ||
                !int.TryParse(match.Groups["end"].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var last) ||
                first < XdataOrigin || last >= XdataOrigin + xram || used > xram))
            {
                throw new StudioXException("BUILD_ARTIFACT", "SDCC 实际 XDATA / PDATA 分配越过已设置或模板保留的内存边界。");
            }
        }
    }

    private static bool TryNumber(string text, out int value) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? int.TryParse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
        : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
