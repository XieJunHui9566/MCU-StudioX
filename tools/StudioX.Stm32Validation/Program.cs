using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;
using System.Text.RegularExpressions;

// 离线真实编译矩阵：只解析 OpenOCD 配置，绝不 init/连接硬件。
// args: toolsets-root output-directory pack [device1,device2,...]
Console.OutputEncoding = Encoding.UTF8;
if (args is ["--language", var runtime, var projects]) return await LanguageChecks.RunAsync(runtime, projects);
var root = Path.GetFullPath(args[1]);
Directory.CreateDirectory(root);
var pack = await new PackRepository(Path.Combine(root, "repository")).ImportAsync(Path.GetFullPath(args[2]));
var tools = await new ToolsetCatalog(args[0]).ResolveAsync("arm.gnu", "1.0.0", "arm-gnu-15.2.rel1");
var environment = ToolsetEnvironment.Create(tools);
var runner = new ProcessRunner();
var requested = args.Length > 3 ? args[3].Split(',').ToHashSet(StringComparer.Ordinal) : null;
// 每种启动文件、SPL 宏、系统频率组合选择最小 Flash/RAM 器件，覆盖最严格的内存上限。
var selected = pack.Manifest.Devices.GroupBy(d => string.Join('|', d.Sources) + string.Join('|', d.Templates.SelectMany(t => t.Build!.Defines)))
    .Select(g => g.OrderBy(d => d.FlashBytes).ThenBy(d => d.RamBytes).First()).ToArray();
if (requested is not null) selected = pack.Manifest.Devices.Where(d => requested.Contains(d.Id)).ToArray();
var results = new List<object>();
var failures = 0;
foreach (var device in selected)
foreach (var template in device.Templates)
{
    var name = device.Id + "_" + template.Id.Replace('-', '_');
    var project = Path.Combine(root, name);
    try
    {
        await new ProjectService().CreateAsync(pack, device.Id, template.Id, name, project);
        var hal = template.Id.StartsWith("hal", StringComparison.Ordinal);
        var rtos = template.Id.Contains("freertos", StringComparison.Ordinal);
        Check(Directory.Exists(Path.Combine(project, "device/sdk/hal")) == hal, "HAL isolation");
        Check(Directory.Exists(Path.Combine(project, "device/sdk/spl")) != hal, "SPL isolation");
        Check(Directory.Exists(Path.Combine(project, "device/sdk/freertos")) == rtos, "FreeRTOS isolation");
        Check(device.Templates.Count == 4, "Expected four templates");
        var build = Path.Combine(project, ".build");
        Directory.CreateDirectory(build);
        var configure = new List<string> { "-S", project, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Debug" };
        foreach (var (key, role) in new[] { ("MAKE_PROGRAM", "ninja"), ("C_COMPILER", "gcc"), ("CXX_COMPILER", "gxx"), ("ASM_COMPILER", "gcc"), ("OBJCOPY", "objcopy"), ("AR", "ar"), ("RANLIB", "ranlib") })
            configure.Add("-DCMAKE_" + key + "=" + tools.Tool(role).Replace('\\', '/'));
        var log = new StringBuilder();
        foreach (var arguments in new[] { configure.ToArray(), new[] { "--build", build, "--parallel", "8" } })
        {
            var r = await Run("cmake", arguments, project);
            log.AppendLine(r.StandardOutput).AppendLine(r.StandardError);
            await File.WriteAllTextAsync(Path.Combine(project, "compile.log"), log.ToString());
            Check(r.Success, "Compiler failed; see compile.log");
        }
        foreach (var extension in new[] { "elf", "bin", "hex", "map" })
            Check(File.Exists(Path.Combine(build, "firmware." + extension)), "Missing " + extension);
        var binary = await File.ReadAllBytesAsync(Path.Combine(build, "firmware.bin"));
        Check(binary.Length <= device.FlashBytes, "Flash overflow");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(binary) == device.RamOrigin + device.RamBytes, "Initial stack pointer");
        var reset = BinaryPrimitives.ReadUInt32LittleEndian(binary.AsSpan(4));
        Check((reset & 1) != 0 && reset >= device.FlashOrigin && reset < device.FlashOrigin + binary.Length, "Reset vector");
        var size = await Run("size", [Path.Combine(build, "firmware.elf")], project);
        var fields = size.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var usedRam = uint.Parse(fields[1]) + uint.Parse(fields[2]);
        Check(usedRam <= device.RamBytes, "RAM overflow");
        if (rtos)
        {
            var symbols = await Run("readelf", ["-sW", Path.Combine(build, "firmware.elf")], project);
            foreach (var (symbol, index) in new[] { ("SVC_Handler", 11), ("PendSV_Handler", 14), ("SysTick_Handler", 15) })
            {
                var line = symbols.StandardOutput.Split('\n').Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    .Single(f => f.Length >= 8 && f[^1] == symbol && f[4] == "GLOBAL" && f[6] != "UND");
                var address = Convert.ToUInt32(line[1], 16) | 1U;
                Check(BinaryPrimitives.ReadUInt32LittleEndian(binary.AsSpan(index * 4)) == address, "RTOS exception vector " + symbol);
            }
        }
        var cfg = await new OpenOcdService(new ToolsetCatalog(args[0])).ConfigurationAsync(project) ?? throw new Exception("Missing OpenOCD profile");
        foreach (var probe in cfg.OpenOcd.Probes)
        {
            var command = OpenOcdService.CreateArguments(project, cfg, new(probe.Id, 2000), tools, Path.Combine(build, "firmware.bin"));
            var script = await File.ReadAllTextAsync(Path.Combine(project, "device", cfg.OpenOcd.TargetScript));
            var expectedId = Regex.Match(script, @"\$id != (0x[0-9a-f]+)").Groups[1].Value;
            var expectedKb = Regex.Match(script, @"\$kb != ([0-9]+)").Groups[1].Value;
            // 用 Tcl 假读数检查身份保护分支，不初始化目标，也不调用下载命令。
            var check = $"set fake_id {expectedId}; set fake_kb {expectedKb}; " +
                "proc read_memory {address width count} { global fake_id fake_kb; if {$width == 32} {return [list $fake_id]}; return [list $fake_kb] }; " +
                "studiox_check_target; set fake_id 0; if {![catch {studiox_check_target}]} {error ID_GUARD_FAILED}; " +
                $"set fake_id {expectedId}; set fake_kb 0; if {{![catch {{studiox_check_target}}]}} {{error SIZE_GUARD_FAILED}}; echo STUDIOX_OFFLINE_OK; shutdown";
            var dry = command[..^2].Concat(new[] { "-c", check }).ToArray();
            var parsed = await Run("openocd", dry, project);
            await File.WriteAllTextAsync(Path.Combine(project, probe.Id + ".log"), parsed.StandardOutput + parsed.StandardError);
            Check(parsed.Success && (parsed.StandardOutput + parsed.StandardError).Contains("STUDIOX_OFFLINE_OK", StringComparison.Ordinal), "OpenOCD parse " + probe.Id);
        }
        results.Add(new { device = device.Id, template = template.Id, success = true, binaryBytes = binary.Length, usedRam, flashBytes = device.FlashBytes, ramBytes = device.RamBytes });
        Console.WriteLine($"PASS {device.Id} {template.Id}: BIN {binary.Length}, RAM {usedRam}/{device.RamBytes}, vectors, SDK isolation, 3 probes (offline)");
    }
    catch (Exception e)
    {
        failures++;
        results.Add(new { device = device.Id, template = template.Id, success = false, error = e.Message });
        Console.WriteLine($"FAIL {device.Id} {template.Id}: {e.Message}");
    }
    await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(results, JsonStore.Options));
}
Console.WriteLine($"Completed {results.Count} combinations, {failures} failures; no hardware accessed.");
return failures == 0 ? 0 : 1;

async Task<ProcessResult> Run(string role, string[] arguments, string cwd) => await runner.RunAsync(new(tools.Tool(role), arguments, cwd,
    TimeSpan.FromMinutes(3), environment, RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
