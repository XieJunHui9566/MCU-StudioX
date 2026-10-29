namespace StudioX.Engine;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>ASlink 的 CODE/XDATA 与片内 RAM 分别计量，不把 SFR、重叠银行和栈当成普通 ELF 段相加。</summary>
internal static class Mx251MemoryAnalyzer
{
    private sealed record ProfileReceipt(string Status, int Schema, string Target, string Model, string FirmwareSha256,
        ulong CodeBytes, ulong StackBase, ulong StackBytes);
    private sealed record Area(string Name, ulong Start, ulong Size, string[] Attributes);
    private static StudioXException Invalid(string message) => new("MX251_MEMORY", message);
    private static ulong Hex(string text) => ulong.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal static async Task<BuildMemoryReport> ReadAsync(string root, ProjectManifest project, CancellationToken token)
    {
        var path = PathBoundary.Resolve(root, BuildReceipt.RelativePath);
        if (!File.Exists(path))
        {
            return new([], "编译成功后显示各存储区占用。");
        }
        var receiptBytes = await File.ReadAllBytesAsync(path, token);
        var receipt = JsonSerializer.Deserialize<BuildReceipt>(receiptBytes, JsonStore.Options)
            ?? throw Invalid("构建凭据为空。");
        if (receipt.Project != project || receipt.SourceStamp is null || receipt.SourceStamp != await Debugging.DebugSourceStamp.ComputeAsync(root, token))
        {
            return new([], "源码或配置已变化，请重新编译后查看 MX251 统计。");
        }
        if (receipt.AnalysisInputs is null)
        {
            return new([], "此旧构建未记录 MX251 分析文件校验值，请重新编译一次。");
        }
        if (receipt.Images.Length != 1 || receipt.Images[0].Format != "ihex")
        {
            throw Invalid("MX251 构建必须包含唯一 HEX。");
        }
        var image = receipt.Images[0];
        var run = Path.GetDirectoryName(image.RelativePath)!.Replace('\\', '/');
        if (!run.StartsWith(".build/mx251/", StringComparison.Ordinal))
        {
            throw Invalid("分析文件不在本次 MX251 构建目录。");
        }
        async Task<byte[]> Read(string relative, string expected)
        {
            var file = PathBoundary.Resolve(root, relative);
            if (new FileInfo(file).Length > 8 * 1024 * 1024)
            {
                throw Invalid("分析文件超过 8 MiB：" + relative);
            }
            var bytes = await File.ReadAllBytesAsync(file, token);
            if (!Hash(bytes).Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid("分析产物与本次构建不一致，请重新编译：" + relative);
            }
            return bytes;
        }
        async Task<string> Input(string name)
        {
            var relative = run + "/" + name;
            if (!receipt.AnalysisInputs.TryGetValue(relative, out var expected))
            {
                throw Invalid("构建未记录分析文件：" + name);
            }
            return Encoding.UTF8.GetString(await Read(relative, expected)).TrimStart('\ufeff');
        }
        await Read(image.RelativePath, image.Sha256);
        var profile = JsonSerializer.Deserialize<ProfileReceipt>(await Input("receipt.json"), JsonStore.Options)
            ?? throw Invalid("缺少 MX251 入口凭据。");
        if (profile.Status != "complete" || profile.Schema != (Mx251SdkProfile.Matches(project) ? 4 : 2) ||
            profile.Target != Mx251SdkProfile.Target(project.DeviceId) ||
            profile.Model is not ("small" or "large") || !image.Sha256.Equals(profile.FirmwareSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("MX251 入口与 IDE 构建凭据不一致。");
        }
        var pack = await JsonStore.ReadAsync<PackManifest>(PathBoundary.Resolve(root, "device/manifest.json"), token);
        var device = pack.Devices.SingleOrDefault(d => d.Id == project.DeviceId)
            ?? throw Invalid("器件包缺少当前器件的存储容量。");
        if (device.Architecture != "mcs251" || device.FlashBytes is 0 or > 131072 || device.RamBytes is 0 or > 65536)
        {
            throw Invalid("器件存储区声明不适用于当前 MX251 入口。");
        }
        var report = Parse(await Input("linked.partial.map"), await Input("linked.partial.mem"), profile, device,
            File.GetLastWriteTimeUtc(PathBoundary.Resolve(root, image.RelativePath)));
        // 分析期间新构建或编辑可能完成；不把前一次结果显示在新工程状态下。
        var latestReceipt = await File.ReadAllBytesAsync(path, token);
        if (!receiptBytes.AsSpan().SequenceEqual(latestReceipt) || project != await ProjectService.ReadAsync(root, token) ||
            receipt.SourceStamp != await Debugging.DebugSourceStamp.ComputeAsync(root, token))
        {
            return new([], "构建或源码正在变化，请稍后重新查看统计。");
        }
        return report;
    }

    private static BuildMemoryReport Parse(string map, string mem, ProfileReceipt receipt, DeviceDefinition device, DateTime built)
    {
        var areas = Regex.Matches(map, @"(?m)^([^\r\n]+?)\s+([0-9A-Fa-f]{8})\s+([0-9A-Fa-f]{8})\s+=\s+\d+\.\s+bytes\s+\(([^)]+)\)")
            .Select(m => new Area(m.Groups[1].Value.Trim(), Hex(m.Groups[2].Value), Hex(m.Groups[3].Value), m.Groups[4].Value.Split(',')))
            .Where(a => a.Size != 0).DistinctBy(a => (a.Name, a.Start, a.Size, string.Join(',', a.Attributes))).ToArray();
        if (areas.Length == 0)
        {
            throw Invalid("MAP 没有可识别的 ASlink 区段。");
        }
        (ulong Used, ulong Maximum) Summary(string name)
        {
            var matches = Regex.Matches(mem, @"(?m)^\s*" + Regex.Escape(name) + @"\s+(?:0x[0-9a-fA-F]+\s+0x[0-9a-fA-F]+\s+)?(\d+)\s+(\d+)\s*$");
            if (matches.Count != 1)
            {
                throw Invalid("MEM 缺少唯一容量统计：" + name);
            }
            return (ulong.Parse(matches[0].Groups[1].Value, CultureInfo.InvariantCulture), ulong.Parse(matches[0].Groups[2].Value, CultureInfo.InvariantCulture));
        }
        var flash = Summary("ROM/EPROM/FLASH");
        var external = Summary("EXTERNAL RAM");
        if (Summary("PAGED EXT. RAM").Used != 0)
        {
            throw Invalid("当前分析器尚不能确认分页 XDATA 的物理映射，未生成不完整统计。");
        }
        var stack = Regex.Match(mem, @"MCS251 stack: \[0x([0-9a-fA-F]+), 0x([0-9a-fA-F]+)\), SPX=0x([0-9a-fA-F]+), reserved=(\d+) bytes, EDATA capacity=(\d+) bytes\.");
        if (!stack.Success)
        {
            throw Invalid("MEM 缺少扩展栈容量声明。");
        }
        var stackBase = Hex(stack.Groups[1].Value);
        var stackEnd = Hex(stack.Groups[2].Value);
        var stackSize = ulong.Parse(stack.Groups[4].Value, CultureInfo.InvariantCulture);
        var edataCapacity = ulong.Parse(stack.Groups[5].Value, CultureInfo.InvariantCulture);
        if (edataCapacity is < 256 or > 65536 || stackSize == 0 || stackBase == 0 || stackEnd != checked(stackBase + stackSize) ||
            stackEnd > edataCapacity || Hex(stack.Groups[3].Value) + 1 != stackBase || stackBase != receipt.StackBase || stackSize != receipt.StackBytes ||
            areas.Count(a => a.Name == "SSEG" && a.Start == stackBase && a.Size == stackSize) != 1)
        {
            throw Invalid("MAP、MEM 与入口凭据中的扩展栈不一致。");
        }
        if (flash.Maximum > device.FlashBytes || flash.Maximum == 0 || external.Maximum > device.RamBytes || external.Maximum == 0 || receipt.CodeBytes > flash.Maximum)
        {
            throw Invalid("链接容量与器件包容量不一致。");
        }
        var code = new bool[checked((int)flash.Maximum)];
        var xdata = new bool[checked((int)external.Maximum)];
        var edata = new bool[checked((int)edataCapacity)];
        static void Mark(bool[] cells, ulong origin, ulong start, ulong size)
        {
            if (start < origin || checked(start + size) > checked(origin + (ulong)cells.Length))
            {
                throw Invalid("MAP 区段超出声明容量。");
            }
            Array.Fill(cells, true, checked((int)(start - origin)), checked((int)size));
        }
        // MEM 的逐字节分配图包含银行/位变量/overlay；DSEG 是汇总跨度，直接求和会把空洞和别名重复计入。
        var rows = Regex.Matches(mem, @"(?m)^0x([0-9a-fA-F]{2}):\|([^\r\n]+)\r?$");
        if (rows.Count != 16 || rows.Select(r => Hex(r.Groups[1].Value)).Distinct().Count() != 16)
        {
            throw Invalid("MEM 片内 RAM 分配图不完整。");
        }
        foreach (Match row in rows)
        {
            var start = Hex(row.Groups[1].Value);
            var cells = row.Groups[2].Value.Split('|');
            if (start % 16 != 0 || cells.Length != 17 || cells[^1] != "")
            {
                throw Invalid("MEM 分配图格式错误。");
            }
            for (var i = 0; i < 16; i++)
            {
                if (cells[i].Length != 1 || !" 0123TBQISAabcdefghijklmnopqrstuvwxyz".Contains(cells[i][0]))
                {
                    throw Invalid("MEM 分配图包含未知标记。");
                }
                edata[checked((int)start + i)] = cells[i] != " ";
            }
        }
        foreach (var area in areas)
        {
            if (area.Attributes.Contains("CODE"))
            {
                Mark(code, device.FlashOrigin, area.Start, area.Size);
            }
            else if (area.Attributes.Contains("XDATA"))
            {
                if (area.Start < edataCapacity)
                {
                    Mark(edata, 0, area.Start, area.Size);
                }
                else
                {
                    Mark(xdata, device.RamOrigin, area.Start, area.Size);
                }
            }
            else if (area.Attributes.Contains("BIT"))
            {
                if (checked(area.Start + area.Size) > 128)
                {
                    throw Invalid("超出已支持的位寻址 RAM 范围。");
                }
            }
            else
            {
                if (checked(area.Start + area.Size) > edataCapacity)
                {
                    throw Invalid("未知或越界的片内 RAM 区段：" + area.Name);
                }
                if (area.Start + area.Size > 256)
                {
                    var start = Math.Max(256UL, area.Start);
                    Mark(edata, 0, start, area.Start + area.Size - start);
                }
            }
        }
        Mark(edata, 0, stackBase, stackSize);
        var codeUsed = (ulong)code.Count(c => c);
        var xUsed = (ulong)xdata.Count(c => c);
        if (receipt.CodeBytes > codeUsed || codeUsed != flash.Used || xUsed != external.Used)
        {
            throw Invalid("MAP 分配与 MEM/HEX 统计不一致，未生成比较快照。");
        }
        return new([new("firmware.hex", "MX251 · " + receipt.Model, built,
            [new("Flash (CODE)", device.FlashOrigin, flash.Maximum, codeUsed),
             new("XDATA", device.RamOrigin, external.Maximum, xUsed),
             new("EDATA（含栈预留）", 0, edataCapacity, (ulong)edata.Count(c => c))])],
            $"静态分配 · HEX {receipt.CodeBytes:N0} B；栈预留 {stackSize:N0} B 已计入 EDATA，非运行时峰值。XDATA 按链接上限 {external.Maximum / 1024} KiB（物理 {device.RamBytes / 1024} KiB）。");
    }
}
