namespace StudioX.Engine;

using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;

public sealed record BuildMemoryRegion(string Name, ulong Origin, ulong Capacity, ulong Used)
{
    public double Percent => Capacity == 0 ? 0 : (double)Used / Capacity * 100;
}

public sealed record BuildMemoryTarget(string Name, string Configuration, DateTime BuiltAtUtc,
    IReadOnlyList<BuildMemoryRegion> Regions, string? Diagnostic = null);
public sealed record BuildMemoryReport(IReadOnlyList<BuildMemoryTarget> Targets, string Message);

/// <summary>容量取自 MEMORY 表；占用取 ELF PT_LOAD 地址区间的并集，独立段之间未分配的空洞不计入。</summary>
public static partial class BuildMemoryAnalyzer
{
    public static async Task<IReadOnlyList<BuildMemoryRegion>> AnalyzeAsync(string elfPath, string mapPath, CancellationToken token = default)
    {
        if (!File.Exists(mapPath)) throw new InvalidDataException("缺少同名 .map 文件，请在链接选项中生成 MAP 后重新编译。");
        if (new FileInfo(mapPath).Length > 128L * 1024 * 1024) throw new InvalidDataException("MAP 文件过大，暂不分析。");
        var regions = new List<BuildMemoryRegion>();
        using (var reader = new StreamReader(mapPath))
        {
            var inMemory = false;
            while (await reader.ReadLineAsync(token) is { } line)
            {
                if (line.Trim() == "Memory Configuration") { inMemory = true; continue; }
                if (!inMemory) continue;
                if (line.StartsWith("Linker script and memory map", StringComparison.Ordinal)) break;
                var match = RegionLine().Match(line);
                if (!match.Success || match.Groups[1].Value == "*default*") continue;
                var origin = ulong.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var length = ulong.Parse(match.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                _ = checked(origin + length);
                regions.Add(new(match.Groups[1].Value, origin, length, 0));
            }
        }
        if (regions.Count == 0) throw new InvalidDataException("MAP 中没有 GNU ld MEMORY 区域表，无法确定各区域容量。");
        for (var i = 0; i < regions.Count; i++)
        for (var j = i + 1; j < regions.Count; j++)
            if (regions[i].Origin < regions[j].Origin + regions[j].Capacity && regions[j].Origin < regions[i].Origin + regions[i].Capacity)
                throw new InvalidDataException("链接内存区域存在重叠或别名，无法唯一归属占用。");
        if (new FileInfo(elfPath).Length > 128L * 1024 * 1024) throw new InvalidDataException("ELF 文件过大，暂不分析。");
        var elf = await File.ReadAllBytesAsync(elfPath, token);
        return AnalyzeElf(elf, regions);
    }

    private static IReadOnlyList<BuildMemoryRegion> AnalyzeElf(byte[] elf, List<BuildMemoryRegion> regions)
    {
        if (elf.Length < 52 || elf[0] != 0x7f || elf[1] != 'E' || elf[2] != 'L' || elf[3] != 'F' ||
            elf[4] is not (1 or 2) || elf[5] != 1 || U16(elf, 16) != 2)
            throw new InvalidDataException("仅支持小端 ELF32 / ELF64 可执行固件。");
        var wide = elf[4] == 2;
        if (wide && elf.Length < 64) throw new InvalidDataException("ELF 文件头不完整。");
        var offset = wide ? U64(elf, 32) : U32(elf, 28);
        var stride = U16(elf, wide ? 54 : 42);
        var count = U16(elf, wide ? 56 : 44);
        if (count is 0 or 0xffff || stride < (wide ? 56 : 32) || checked(offset + (ulong)stride * count) > (ulong)elf.Length)
            throw new InvalidDataException("ELF 加载段表无效或不受支持。");
        var loads = 0;
        var allocations = regions.Select(_ => new List<(ulong Start, ulong End)>()).ToArray();
        for (var i = 0; i < count; i++)
        {
            var row = checked((int)(offset + (ulong)i * stride));
            if (U32(elf, row) != 1) continue;
            var fileOffset = wide ? U64(elf, row + 8) : U32(elf, row + 4);
            var virtualAddress = wide ? U64(elf, row + 16) : U32(elf, row + 8);
            var physicalAddress = wide ? U64(elf, row + 24) : U32(elf, row + 12);
            var fileSize = wide ? U64(elf, row + 32) : U32(elf, row + 16);
            var memorySize = wide ? U64(elf, row + 40) : U32(elf, row + 20);
            if (fileSize > memorySize || checked(fileOffset + fileSize) > (ulong)elf.Length)
                throw new InvalidDataException("ELF 加载段越界。");
            // VMA 包括 BSS、NOLOAD、堆栈预留；LMA 只计有文件内容的字节，避免把 BSS 算进 Flash。
            Include(virtualAddress, memorySize);
            if (physicalAddress != virtualAddress) Include(physicalAddress, fileSize);
            loads++;
        }
        if (loads == 0) throw new InvalidDataException("ELF 中没有加载段。");
        for (var index = 0; index < regions.Count; index++)
        {
            // 顶部固定栈与底部数据之间通常留给动态堆；不能用最高地址把整片 RAM 算满。
            // 合并重叠范围，避免同一区域的 VMA、LMA 或重叠加载段重复计数。
            ulong used = 0, end = 0;
            foreach (var interval in allocations[index].OrderBy(a => a.Start))
            {
                var start = Math.Max(end, interval.Start);
                if (interval.End > start) used = checked(used + interval.End - start);
                end = Math.Max(end, interval.End);
            }
            regions[index] = regions[index] with { Used = used };
        }
        return regions;

        void Include(ulong start, ulong length)
        {
            if (length == 0) return;
            var end = checked(start + length);
            var index = regions.FindIndex(r => start >= r.Origin && start < r.Origin + r.Capacity);
            if (index < 0) throw new InvalidDataException($"加载段 0x{start:X} 没有对应的 MEMORY 区域。");
            if (end > regions[index].Origin + regions[index].Capacity)
                throw new InvalidDataException($"加载段 0x{start:X} 超出 {regions[index].Name} 区域容量。");
            allocations[index].Add((start, end));
        }
    }
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));
    [GeneratedRegex(@"^\s*(\S+)\s+0x([0-9a-fA-F]+)\s+0x([0-9a-fA-F]+)(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex RegionLine();
}
