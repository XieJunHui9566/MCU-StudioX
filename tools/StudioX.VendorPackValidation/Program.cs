using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;
using StudioX.Packages;

// 所有 OpenOCD 调用均以 noinit 启动；没有 USB 枚举、目标连接或 Flash 操作。
// dotnet run --project tools/StudioX.VendorPackValidation -c Release -- <tool-runtime> <new-output-directory> <pack.mcupack> [more packs...]
Console.OutputEncoding = Encoding.UTF8;
if (args.Length < 3)
{
    Console.Error.WriteLine("用法：StudioX.VendorPackValidation <tool-runtime> <新输出目录> <芯片包.mcupack> [更多芯片包...]");
    return 2;
}

var runtime = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (Directory.Exists(output) || File.Exists(output)) throw new InvalidOperationException("验收输出目录必须尚不存在。");
Directory.CreateDirectory(output);
var catalog = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
var builds = new BuildService(catalog);
var downloads = new OpenOcdService(catalog);
var repository = new PackRepository(Path.Combine(output, "repository"));
var rows = new List<ValidationRow>();
var totalDevices = 0;
var createdProjects = 0;
var totalBuilds = 0;
var totalParses = 0;
var totalDebugPlans = 0;

foreach (var archive in args.Skip(2))
{
    var pack = await repository.ImportAsync(Path.GetFullPath(archive));
    var devices = pack.Manifest.Devices;
    var representatives = SelectBuilds(devices);
    Console.WriteLine($"检查 {pack.Manifest.Id} {pack.Manifest.Version}：{devices.Count} 个型号，{representatives.Count} 个编译组合。");
    foreach (var device in devices)
    {
        totalDevices++;
        var selectedTemplates = representatives.Where(x => x.DeviceId == device.Id).Select(x => x.TemplateId).ToHashSet(StringComparer.Ordinal);
        var primaryTemplate = device.Templates[0];
        selectedTemplates.Add(primaryTemplate.Id); // 每个料号都实际创建一次工程。
        foreach (var template in device.Templates.Where(t => selectedTemplates.Contains(t.Id)))
        {
            var compile = representatives.Contains((device.Id, template.Id));
            var project = Path.Combine(output, "projects", pack.Manifest.Id, device.Id, template.Id);
            var model = $"{pack.Manifest.Id}/{device.Id}/{template.Id}";
            try
            {
                ValidateDefinition(pack, device, template);
                var projectManifest = await new ProjectService().CreateAsync(pack, device.Id, template.Id,
                    "check_" + device.Id, project);
                createdProjects++;
                Check(projectManifest.DeviceId == device.Id && projectManifest.TemplateId == template.Id, "生成工程器件/模板不匹配。");
                Check(File.Exists(Path.Combine(project, "device", device.LinkerScript)), "生成工程缺少链接脚本。");
                Check(File.Exists(Path.Combine(project, "src", "main.c")), "生成工程缺少用户入口。");
                foreach (var source in StartupSources(device, template))
                    Check(File.Exists(Path.Combine(project, "device", source)), "生成工程缺少启动/向量文件：" + source);

                var config = await downloads.ConfigurationAsync(project);
                var debugPlans = 0;
                if (device.OpenOcd is not null)
                {
                    Check(config is not null, "器件声明 OpenOCD，但 IDE 未产生下载配置。");
                    var tools = await catalog.ResolveAsync(device.ToolsetId, device.ToolsetVersion, device.CompilerId);
                    var checkedPlans = await CheckDebugPlansAsync(project, config!, tools);
                    totalParses += checkedPlans.Parsed;
                    totalDebugPlans += debugPlans = checkedPlans.DebugPlans;
                }
                else Check(config is null, "未声明 OpenOCD 的器件意外开放下载。");

                long binaryBytes = 0;
                if (compile)
                {
                    var report = await builds.BuildAsync(project);
                    Check(report.Success, "真实构建失败；见 " + report.LogPath);
                    var binary = await File.ReadAllBytesAsync(Path.Combine(project, ".build", "firmware.bin"));
                    var elfPath = Path.Combine(project, ".build", "firmware.elf");
                    Check(File.Exists(elfPath) && File.Exists(Path.Combine(project, ".build", "firmware.hex")),
                        "缺少 ELF/HEX 产物。");
                    ValidateImage(binary, await File.ReadAllBytesAsync(elfPath), device);
                    if (config is not null)
                    {
                        var prepared = await downloads.PrepareAsync(project, config.Options);
                        Check(prepared.Arguments.Any(a => a.Contains("studiox_check_target", StringComparison.Ordinal)),
                            "下载计划未在写入前核对目标。");
                    }
                    binaryBytes = binary.Length;
                    totalBuilds++;
                }
                if (!compile)
                {
                    // 只清理由本程序刚创建且已确认在本次输出根目录内的工程副本。
                    var checkedProject = PathBoundary.Resolve(output, Path.GetRelativePath(output, project).Replace('\\', '/'));
                    Check(checkedProject == Path.GetFullPath(project), "工程清理路径不在验收输出目录内。");
                    Directory.Delete(checkedProject, recursive: true);
                }
                rows.Add(new(model, compile, device.FlashBytes, device.RamBytes, binaryBytes, config is not null,
                    config?.OpenOcd.Probes.Count ?? 0, debugPlans, "PASS", null));
                Console.WriteLine($"PASS {model}：工程、器件定义{(compile ? $"、编译 {binaryBytes} B" : "")}{(config is not null ? "、OpenOCD noinit" : "")}");
            }
            catch (Exception ex)
            {
                rows.Add(new(model, compile, device.FlashBytes, device.RamBytes, 0, device.OpenOcd is not null,
                    device.OpenOcd?.Probes.Count ?? 0, 0, "FAIL", ex.ToString()));
                Console.Error.WriteLine("FAIL " + model + "：" + ex.Message);
            }
            await File.WriteAllTextAsync(Path.Combine(output, "matrix.json"), JsonSerializer.Serialize(rows, JsonStore.Options));
        }
    }
}

var failures = rows.Count(r => r.Status == "FAIL");
var summary = $"{(failures == 0 ? "PASS" : "FAIL")}：{totalDevices} 个型号，{createdProjects} 次工程创建，{totalBuilds} 次代表性真实编译，{totalParses} 次 OpenOCD noinit 解析，{totalDebugPlans} 个可用调试计划，{failures} 个失败。未连接或访问硬件；下载与实板调试未验证。";
await File.WriteAllTextAsync(Path.Combine(output, "result.txt"), summary);
Console.WriteLine(summary);
return failures == 0 ? 0 : 1;

static HashSet<(string DeviceId, string TemplateId)> SelectBuilds(IReadOnlyList<DeviceDefinition> devices)
{
    // 同一容量、内核、启动/SDK 组合和模板编译一次；封装变体仍逐一创建工程和检查配置。
    var groups = devices.SelectMany(d => d.Templates.Select(t => (Device: d, Template: t)))
        .GroupBy(x => JsonSerializer.Serialize(new
        {
            x.Device.Architecture, x.Device.ToolsetId, x.Device.ToolsetVersion, x.Device.CompilerId,
            x.Device.FlashBytes, x.Device.RamOrigin, x.Device.RamBytes, x.Device.CpuFlags,
            x.Device.Defines, x.Device.Sources, x.Device.CompileOptions, x.Device.LinkOptions,
            Startup = StartupSources(x.Device, x.Template),
            x.Template.Id, x.Template.Build
        }, JsonStore.Options), StringComparer.Ordinal);
    return groups.Select(g => g.OrderBy(x => x.Device.Id, StringComparer.Ordinal).First())
        .Select(x => (x.Device.Id, x.Template.Id)).ToHashSet();
}

static string[] StartupSources(DeviceDefinition device, ProjectTemplate template) =>
    device.Sources.Concat(template.Build?.Sources ?? [])
        .Where(path => path.Contains("startup", StringComparison.OrdinalIgnoreCase) ||
                       path.Contains("vector", StringComparison.OrdinalIgnoreCase) ||
                       Path.GetExtension(path).Equals(".s", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

static void ValidateDefinition(InstalledPack pack, DeviceDefinition device, ProjectTemplate template)
{
    Check(device.Architecture is "arm" or "riscv", "只支持 ARM 或 RISC-V 器件。");
    Check(device.FlashBytes > 0 && device.RamBytes > 0, "Flash/RAM 容量无效。");
    Check((ulong)device.FlashOrigin + device.FlashBytes <= uint.MaxValue + 1UL &&
          (ulong)device.RamOrigin + device.RamBytes <= uint.MaxValue + 1UL, "存储范围溢出。");
    Check(device.Architecture == "arm"
            ? device.CpuFlags.Any(x => x.StartsWith("-mcpu=cortex-m", StringComparison.Ordinal))
            : device.CpuFlags.Any(x => x.StartsWith("-march=rv32", StringComparison.Ordinal)) &&
              device.CpuFlags.Any(x => x.StartsWith("-mabi=", StringComparison.Ordinal)),
        "缺少与架构匹配的 CPU/ABI 参数。");
    Check(File.Exists(Path.Combine(pack.RootDirectory, device.LinkerScript)), "包内链接脚本缺失。");
    ValidateLinkerMemory(File.ReadAllText(Path.Combine(pack.RootDirectory, device.LinkerScript)), device);
    Check(StartupSources(device, template).Length > 0, "缺少明确启动/向量源文件。");
    Check(File.Exists(Path.Combine(pack.RootDirectory, template.EntryFile)), "模板入口缺失。");
}

static void ValidateLinkerMemory(string script, DeviceDefinition device)
{
    // 解析 MEMORY 字面量及常见 PROVIDE/符号常量；不执行链接脚本或猜测算术表达式。
    var symbols = Regex.Matches(script,
        @"(?im)^\s*(?:PROVIDE\s*\(\s*)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>0x[0-9a-f]+|[0-9]+[km]?)\s*\)?\s*;")
        .Cast<Match>().ToDictionary(match => match.Groups["name"].Value,
            match => ParseSize(match.Groups["value"].Value), StringComparer.Ordinal);
    var regions = Regex.Matches(script,
        @"(?im)^\s*[A-Za-z_][A-Za-z0-9_]*\s*(?:\([^)]*\))?\s*:\s*ORIGIN\s*=\s*(?<origin>0x[0-9a-f]+|[0-9]+|[A-Za-z_][A-Za-z0-9_]*)\s*,\s*LENGTH\s*=\s*(?<length>0x[0-9a-f]+|[0-9]+[km]?|[A-Za-z_][A-Za-z0-9_]*)\b")
        .Cast<Match>()
        .Select(match => (Origin: Resolve(match.Groups["origin"].Value),
            Bytes: Resolve(match.Groups["length"].Value)))
        .ToArray();
    var flash = regions.Where(x => x.Origin == device.FlashOrigin).Select(x => x.Bytes).ToArray();
    var ram = regions.Where(x => x.Origin == device.RamOrigin).Select(x => x.Bytes).ToArray();
    Check(flash.Contains(device.OpenOcd?.ApplicationFlashBytes ?? device.FlashBytes),
        $"链接脚本 Flash 起点/长度与清单不一致：0x{device.FlashOrigin:x8} / {device.FlashBytes} B。");
    Check(ram.Contains(device.RamBytes),
        $"链接脚本 RAM 起点/长度与清单不一致：0x{device.RamOrigin:x8} / {device.RamBytes} B。");

    uint Resolve(string expression) => symbols.TryGetValue(expression, out var value) ? value : ParseSize(expression);
}

static uint ParseSize(string text)
{
    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return Convert.ToUInt32(text[2..], 16);
    var multiplier = text[^1] switch { 'k' or 'K' => 1024U, 'm' or 'M' => 1024U * 1024U, _ => 1U };
    return checked(uint.Parse(multiplier == 1 ? text : text[..^1]) * multiplier);
}

static void ValidateImage(byte[] image, byte[] elf, DeviceDefinition device)
{
    Check(image.Length > 0 && image.Length <= device.FlashBytes, "BIN 为空或超过器件 Flash。");
    Check(elf.Length >= 52 && elf[0] == 0x7f && elf[1] == 'E' && elf[2] == 'L' && elf[3] == 'F' &&
          elf[4] == 1 && elf[5] == 1 && elf[6] == 1, "需要 32 位小端 ELF。");
    var machine = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(18));
    Check(machine == (device.Architecture == "arm" ? 40 : 243), "ELF 目标架构与器件定义不同。");
    var entry = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan(24));
    var programOffset = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan(28));
    var programSize = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(42));
    var programCount = BinaryPrimitives.ReadUInt16LittleEndian(elf.AsSpan(44));
    Check(programSize == 32 && programCount > 0 &&
          (ulong)programOffset + (ulong)programSize * programCount <= (ulong)elf.Length, "ELF 装载表无效。");
    var hasEntrySegment = false;
    for (var index = 0; index < programCount; index++)
    {
        var offset = checked((int)programOffset + index * programSize);
        if (BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan(offset)) != 1) continue;
        var fileOffset = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan(offset + 4));
        var address = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan(offset + 12));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan(offset + 16));
        var memoryLength = BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan(offset + 20));
        if (length == 0) continue;
        var end = (ulong)address + length;
        Check(length <= memoryLength && (ulong)fileOffset + length <= (ulong)elf.Length &&
              address >= device.FlashOrigin && end <= (ulong)device.FlashOrigin + device.FlashBytes,
            "ELF 装载段超出 Flash 或文件边界。");
        hasEntrySegment |= (entry & (device.Architecture == "arm" ? ~1U : uint.MaxValue)) >= address &&
                           (ulong)(entry & (device.Architecture == "arm" ? ~1U : uint.MaxValue)) < end;
    }
    Check(hasEntrySegment, "ELF 入口未处于 Flash 装载段。");
    if (device.Architecture != "arm") return; // RISC-V 启动代码不是 Cortex-M 的 SP/Reset 双向量。
    Check(image.Length >= 8, "Cortex-M BIN 缺少 SP/Reset 双向量。");
    var stack = BinaryPrimitives.ReadUInt32LittleEndian(image);
    var reset = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(4));
    Check(stack >= device.RamOrigin && (ulong)stack <= (ulong)device.RamOrigin + device.RamBytes && stack % 8 == 0,
        $"初始栈地址 0x{stack:x8} 超出 RAM 或未按 8 字节对齐。");
    Check((reset & 1) == 1 && (reset & ~1U) >= device.FlashOrigin &&
          (ulong)(reset & ~1U) < (ulong)device.FlashOrigin + (ulong)image.Length, "复位向量不是当前固件 Flash 内的 Thumb 地址。");
}

static async Task<(int Parsed, int DebugPlans)> CheckDebugPlansAsync(string project, DownloadConfiguration config, ResolvedToolset tools)
{
    Check(DebugTargetProfile.Find(config.Device) is not null, "器件声明 OpenOCD，但调试器尚未支持其精确型号/内存布局。");
    var runner = new ProcessRunner();
    var count = 0;
    var debugPlans = 0;
    foreach (var probe in config.OpenOcd.Probes)
    {
        var selected = config with { Options = new(probe.Id, probe.DefaultSpeedKhz) };
        try
        {
            var plan = OpenOcdDebugPlanner.Create(project, selected, tools, Path.Combine(project, "firmware.elf"));
            Check(plan.InitializeCommands.Any(x => x.Contains("monitor studiox_check_target", StringComparison.Ordinal)),
                "调试计划没有目标身份检查。");
            Check(plan.InitializeCommands.Any(x => x.Contains("monitor verify_image", StringComparison.Ordinal)) &&
                  plan.InitializeCommands.All(x => !x.Contains("target-download", StringComparison.Ordinal)),
                "调试计划未校验板上 ELF，或隐式下载。");
            debugPlans++;
        }
        catch (StudioXException ex) when (ex.Code == "DEBUG_PROBE")
        {
            // 某些既有包含仅供下载的 J-Link；按实际能力统计，不宣称可调试。
        }
        var download = OpenOcdService.CreateArguments(project, selected, selected.Options, tools,
            Path.Combine(project, "firmware.bin"));
        // noinit 必须在读取接口/目标脚本前配置；脚本解析后只检查 Tcl 过程定义，不执行它。
        var command = new[] { "-c", "noinit" }.Concat(download[..^2])
            .Concat(["-c", "if {[llength [info procs studiox_check_target]] != 1} {error MISSING_IDENTITY_GUARD}; echo STUDIOX_OFFLINE_OK; shutdown"])
            .ToArray();
        var result = await runner.RunAsync(new(tools.Tool("openocd"), command, project, TimeSpan.FromSeconds(20),
            ToolsetEnvironment.Create(tools), RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
        var log = result.StandardOutput + result.StandardError;
        await File.WriteAllTextAsync(Path.Combine(project, $"openocd-{probe.Id}.log"), log);
        Check(result.Success && log.Contains("STUDIOX_OFFLINE_OK", StringComparison.Ordinal),
            "OpenOCD noinit 解析失败；见 openocd-" + probe.Id + ".log");
        count++;
    }
    return (count, debugPlans);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal sealed record ValidationRow(string Model, bool Compiled, uint FlashBytes, uint RamBytes,
    long BinaryBytes, bool HasOpenOcd, int Probes, int DebugPlans, string Status, string? Error);
