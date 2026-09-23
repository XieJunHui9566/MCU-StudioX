namespace StudioX.Engine;

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>STC 器件包声明的物理程序空间和链接器可用上限（可能包含保留区）。</summary>
public sealed record StcCodeRomLimit(int PhysicalBytes, int MaximumBytes)
{
    public int ReservedBytes => PhysicalBytes - MaximumBytes;

    public void Validate(int? codeRomSizeBytes)
    {
        if (codeRomSizeBytes is { } size && (size < 1024 || size > MaximumBytes))
            throw new StudioXException("BUILD_SETTINGS", $"代码 ROM 大小必须在 1024–{MaximumBytes} 字节之间；器件包已扣除 {ReservedBytes} 字节保留区。");
    }

    public static async Task<StcCodeRomLimit?> ReadAsync(string root, ProjectManifest project, CancellationToken token = default)
    {
        if (project.ToolsetId != "stc.sdcc") return null;
        var path = PathBoundary.Resolve(root, "device/manifest.json");
        PackManifest pack;
        try { pack = await JsonStore.ReadAsync<PackManifest>(path, token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { throw new StudioXException("BUILD_SETTINGS", "无法读取 STC 器件包的型号与程序容量：" + ex.Message); }
        if (pack.FormatVersion != 1 || pack.Id != project.PackId || pack.Version != project.PackVersion)
            throw new StudioXException("BUILD_SETTINGS", "工程中的 STC 器件包清单与工程型号不匹配。");
        var device = pack.Devices.SingleOrDefault(d => d.Id == project.DeviceId);
        if (device is null || device.ToolsetId != project.ToolsetId || device.Architecture != "mcs51" || device.FlashBytes > int.MaxValue)
            throw new StudioXException("BUILD_SETTINGS", "STC 器件包缺少有效的程序容量。");
        var options = device.LinkOptions;
        var indexes = Enumerable.Range(0, options.Count).Where(i => options[i] == "--code-size").ToArray();
        if (indexes.Length != 1 || indexes[0] + 1 >= options.Count ||
            !int.TryParse(options[indexes[0] + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var maximum) ||
            maximum < 1024 || maximum > device.FlashBytes)
            throw new StudioXException("BUILD_SETTINGS", "STC 器件包缺少有效的 --code-size 上限。");
        return new StcCodeRomLimit(checked((int)device.FlashBytes), maximum);
    }

    public async Task VerifyBuildAsync(string build, int? selectedBytes, CancellationToken token = default)
    {
        var expected = selectedBytes ?? MaximumBytes;
        var memPath = Path.Combine(build, "firmware.mem");
        if (!File.Exists(memPath)) throw new StudioXException("BUILD_ARTIFACT", "SDCC 构建缺少 .mem 容量统计。");
        var rows = (await File.ReadAllLinesAsync(memPath, token))
            .Where(line => line.TrimStart().StartsWith("ROM/EPROM/FLASH", StringComparison.Ordinal)).ToArray();
        var match = rows.Length == 1 ? Regex.Match(rows[0], @"\s(?<used>\d+)\s+(?<max>\d+)\s*$", RegexOptions.CultureInvariant) : Match.Empty;
        if (!match.Success || !int.TryParse(match.Groups["max"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var actual) || actual != expected)
            throw new StudioXException("BUILD_SETTINGS_NOT_APPLIED", $"SDCC .mem 的程序容量与设置不符；期望 {expected} 字节。");

        // .mem 的 Max 是链接器参数，另以实际 Intel HEX 地址检验没有越过可用区。
        uint upper = 0;
        var hasData = false;
        var ended = false;
        foreach (var line in await File.ReadAllLinesAsync(Path.Combine(build, "firmware.hex"), token))
        {
            if (ended || line.Length < 11 || line[0] != ':')
                throw new StudioXException("BUILD_ARTIFACT", "SDCC Intel HEX 记录无效。");
            byte[] record;
            try { record = Convert.FromHexString(line[1..]); }
            catch (FormatException) { throw new StudioXException("BUILD_ARTIFACT", "SDCC Intel HEX 记录不是十六进制。"); }
            if (record.Length != record[0] + 5 || record.Sum(value => (int)value) % 256 != 0)
                throw new StudioXException("BUILD_ARTIFACT", "SDCC Intel HEX 长度或校验和无效。");
            var address = (uint)(record[1] << 8 | record[2]);
            switch (record[3])
            {
                case 0:
                    if (upper + address + record[0] > (uint)expected)
                        throw new StudioXException("BUILD_ARTIFACT", "SDCC 程序代码越过设置的 ROM 上限或器件保留区。");
                    hasData = true;
                    break;
                case 1 when record[0] == 0:
                    ended = true;
                    break;
                case 4 when record[0] == 2:
                    upper = (uint)(record[4] << 8 | record[5]) << 16;
                    break;
                default:
                    throw new StudioXException("BUILD_ARTIFACT", "SDCC Intel HEX 使用了未识别的记录类型。");
            }
        }
        if (!ended || !hasData) throw new StudioXException("BUILD_ARTIFACT", "SDCC Intel HEX 缺少代码或结束记录。");
    }
}
