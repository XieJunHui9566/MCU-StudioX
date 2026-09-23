using System.Buffers.Binary;
using StudioX.Engine;
using StudioX.Foundation;

if (args.Length < 1 || args.Length % 2 != 1) throw new ArgumentException("Usage: BuildMemoryValidation <new-output-directory> [elf map]...");
var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root)) throw new ArgumentException("Output must be a new directory.");
Directory.CreateDirectory(Path.Combine(root, ".build"));
var mapPath = Path.Combine(root, ".build", "firmware.map");
var elfPath = Path.Combine(root, ".build", "firmware.elf");
var map = """
Discarded input sections
 .text.dead 0x00000000 0x100
Memory Configuration
Name Origin Length Attributes
FLASH 0x08000000 0x00001000 xr
RAM 0x20000000 0x00001000 xrw
CCMRAM 0x10000000 0x00001000 rw
*default* 0x00000000 0xffffffff
Linker script and memory map
.debug_info 0x00000000 0xffffff
""";
await File.WriteAllTextAsync(mapPath, map);
var elf = new byte[1024];
elf[0] = 0x7f; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F'; elf[4] = 1; elf[5] = 1;
U16(16, 2); U32(28, 52); U16(42, 32); U16(44, 4);
Segment(0, 1, 0x08000000, 0x08000000, 0x100, 0x100);
Segment(1, 1, 0x20000000, 0x08000100, 0x10, 0x50);
Segment(2, 1, 0x20000100, 0x08000110, 0, 0x80);
Segment(3, 4, 0, 0, 0x100, 0x100);
await File.WriteAllBytesAsync(elfPath, elf);
var regions = await BuildMemoryAnalyzer.AnalyzeAsync(elfPath, mapPath);
Check(regions.Count == 3 && regions.Single(r => r.Name == "FLASH").Used == 0x110 &&
    regions.Single(r => r.Name == "RAM").Used == 0xd0 && regions.Single(r => r.Name == "CCMRAM").Used == 0,
    "LMA init data, VMA BSS/stack, unallocated holes excluded, empty CCM and discarded/debug exclusion");
Segment(2, 1, 0x20000f80, 0x20000f80, 0, 0x80);
await File.WriteAllBytesAsync(elfPath, elf);
Check((await BuildMemoryAnalyzer.AnalyzeAsync(elfPath, mapPath)).Single(r => r.Name == "RAM").Used == 0xd0,
    "fixed stack at RAM top does not count the free heap gap");
Segment(2, 1, 0x20000040, 0x20000030, 0x20, 0x80);
await File.WriteAllBytesAsync(elfPath, elf);
Check((await BuildMemoryAnalyzer.AnalyzeAsync(elfPath, mapPath)).Single(r => r.Name == "RAM").Used == 0xc0,
    "overlapping VMA/LMA and segments count only once");
Segment(2, 1, 0x20000100, 0x08000110, 0, 0x80);
await File.WriteAllBytesAsync(elfPath, elf);
Segment(2, 1, 0x20000f80, 0x20000f80, 0, 0x100);
await File.WriteAllBytesAsync(elfPath, elf);
await Reject("segment extending past a memory region is rejected");
Segment(2, 1, 0x20000100, 0x08000110, 0, 0x80);
await File.WriteAllBytesAsync(elfPath, elf);
await File.WriteAllTextAsync(mapPath, map.Replace("FLASH", "QSPI_APP").Replace("0x00001000", "0x00000800"));
regions = await BuildMemoryAnalyzer.AnalyzeAsync(elfPath, mapPath);
Check(regions[0].Name == "QSPI_APP" && regions[0].Capacity == 2048, "actual linker region names/capacities, no chip constants");
await File.WriteAllTextAsync(mapPath, map.Replace("0x08000000", "0x00000000"));
Segment(0, 1, 0, 0, 0x100, 0x100); Segment(1, 1, 0x20000000, 0x100, 0x10, 0x50);
await File.WriteAllBytesAsync(elfPath, elf);
Check((await BuildMemoryAnalyzer.AnalyzeAsync(elfPath, mapPath))[0].Used == 0x110, "Flash at address zero");
await File.WriteAllTextAsync(mapPath, map);
Segment(0, 1, 0x08000000, 0x08000000, 0x100, 0x100); Segment(1, 1, 0x20000000, 0x08000100, 0x10, 0x50);
await File.WriteAllBytesAsync(elfPath, elf);
await File.WriteAllTextAsync(mapPath, map.Replace("0x10000000", "0x20000000"));
await Reject("overlapping/aliased regions are not guessed");
await File.WriteAllTextAsync(mapPath, "not a GNU map"); await Reject("missing MEMORY table");
await File.WriteAllTextAsync(mapPath, map); U32(28, 0xfffffff0); await File.WriteAllBytesAsync(elfPath, elf);
await Reject("truncated/overflowed ELF header"); U32(28, 52); await File.WriteAllBytesAsync(elfPath, elf);

var project = new ProjectManifest(1, "memory_test", "test.pack", "1.0.0", new string('0', 64), "test-device", "bare", "test.toolset", "1.0.0", "gcc");
await JsonStore.WriteAsync(Path.Combine(root, ".studiox", "project.json"), project);
var receiptPath = Path.Combine(root, ".build", "studiox-build-receipt.json");
await JsonStore.WriteAsync(receiptPath, new { Project = project, ToolFingerprint = "test", Images = new[] { new { RelativePath = ".build/firmware.elf", Format = "elf", Sha256 = "test", SymbolsPath = ".build/firmware.elf" } } });
await File.WriteAllTextAsync(Path.Combine(root, ".build", "CMakeCache.txt"), "CMAKE_BUILD_TYPE:STRING=Release\n");
var service = new BuildMemoryService();
var report = await service.ReadAsync(root);
Check(report.Targets.Single().Regions.Count == 3 && report.Targets[0].Configuration == "Release", "report uses actual build configuration");
var snapshotPath = Path.Combine(root, ".build", "studiox-memory.json");
var oldSnapshot = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath))!;
oldSnapshot["analysisVersion"] = 0;
oldSnapshot["report"]!["message"] = "stale address high-water statistic";
await File.WriteAllTextAsync(snapshotPath, oldSnapshot.ToJsonString());
Check((await service.ReadAsync(root)).Message != "stale address high-water statistic", "old algorithm snapshot automatically recalculated");
File.Delete(receiptPath);
Check((await service.ReadAsync(root)).Targets.Single().Regions.Count == 3, "cached successful snapshot survives configure-only receipt invalidation");
await File.AppendAllTextAsync(mapPath, "\n");
Check((await service.ReadAsync(root)).Targets.Count == 0, "changed artifact invalidates cached snapshot");
try { await new BuildService(new ToolsetCatalog(Path.Combine(root, "missing-tools"))).BuildAsync(root); }
catch (StudioXException) { }
Check(!File.Exists(Path.Combine(root, ".build", "studiox-memory.json")) && (await service.ReadAsync(root)).Targets.Count == 0,
    "failed build invalidates previous snapshot");

for (var i = 1; i < args.Length; i += 2)
{
    var actual = await BuildMemoryAnalyzer.AnalyzeAsync(args[i], args[i + 1]);
    Console.WriteLine(Path.GetFileName(args[i]) + ": " + string.Join(", ", actual.Select(r => $"{r.Name} {r.Used}/{r.Capacity} B ({r.Percent:F2}%)")));
    await JsonStore.WriteAsync(Path.Combine(root, $"actual-{i}.json"), actual);
}
await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), "PASS: ELF/MAP allocation, LMA/VMA, NOBITS, holes, custom regions, zero origin, malformed and missing data, snapshot invalidation.\n");

void Check(bool okay, string description) { if (!okay) throw new InvalidOperationException(description); Console.WriteLine("PASS: " + description); }
async Task Reject(string description)
{
    try { await BuildMemoryAnalyzer.AnalyzeAsync(elfPath, mapPath); throw new InvalidOperationException(description); }
    catch (InvalidDataException) { Console.WriteLine("PASS: " + description); }
}
void U16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(elf.AsSpan(offset), value);
void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(elf.AsSpan(offset), value);
void Segment(int index, uint type, uint vma, uint lma, uint fileSize, uint memorySize)
{
    var offset = 52 + 32 * index;
    U32(offset, type); U32(offset + 4, 512); U32(offset + 8, vma); U32(offset + 12, lma);
    U32(offset + 16, fileSize); U32(offset + 20, memorySize);
}
