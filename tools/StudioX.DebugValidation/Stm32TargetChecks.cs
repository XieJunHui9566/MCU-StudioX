using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;
using StudioX.Packages;

internal static class Stm32TargetChecks
{
    public static async Task<int> RunAsync(string packDirectory, string runtime, string output)
    {
        var root = Path.GetFullPath(output);
        if (Directory.Exists(root)) throw new InvalidOperationException("Use a new validation directory.");
        Directory.CreateDirectory(root);
        var toolRoot = Path.GetFullPath(Path.Combine(runtime, "toolsets/arm.gnu/1.0.0"));
        var tools = new ResolvedToolset(await JsonStore.ReadAsync<ToolsetManifest>(Path.Combine(toolRoot, "toolset.json")), toolRoot, "offline-configuration-only");
        var downloads = new OpenOcdService(new ToolsetCatalog(Path.Combine(runtime, "toolsets")));
        var runner = new ProcessRunner();
        var environment = ToolsetEnvironment.Create(tools);
        var rows = new List<object>();
        var models = new HashSet<string>();
        int packs = 0, plans = 0, parses = 0, templates = 0;
        DownloadConfiguration? reference = null;
        foreach (var archive in Directory.EnumerateFiles(packDirectory, "*.mcupack").Order())
        {
            using var zip = ZipFile.OpenRead(archive);
            async Task<string> Read(string path)
            {
                using var reader = new StreamReader(zip.GetEntry(path)!.Open());
                return await reader.ReadToEndAsync();
            }
            var manifest = JsonSerializer.Deserialize<PackManifest>(await Read("manifest.json"), JsonStore.Options)!;
            var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(await Read("files.sha256.json"), JsonStore.Options)!;
            foreach (var device in manifest.Devices)
            {
                Check(models.Add(device.Id), "Duplicate model " + device.Id);
                var expectedCore = device.CpuFlags.Contains("-mcpu=cortex-m3") ? "Cortex-M3" : "Cortex-M4";
                Check(device.CpuFlags.Contains(expectedCore == "Cortex-M3" ? "-mcpu=cortex-m3" : "-mcpu=cortex-m4"), "Pack declares CPU");
                var script = await Read(device.OpenOcd!.TargetScript);
                using (var stream = zip.GetEntry(device.OpenOcd.TargetScript)!.Open())
                    Check(Convert.ToHexString(SHA256.HashData(stream)).Equals(hashes[device.OpenOcd.TargetScript], StringComparison.OrdinalIgnoreCase), "Target script hash");
                var packProject = Path.Combine(root, "pack", device.Id);
                var cubeProject = Path.Combine(root, "cubemx", device.Id);
                var project = new ProjectManifest(1, device.Id, manifest.Id, manifest.Version, "offline", device.Id,
                    device.Templates[0].Id, device.ToolsetId, device.ToolsetVersion, device.CompilerId);
                await JsonStore.WriteAsync(Path.Combine(packProject, ".studiox/project.json"), project);
                await JsonStore.WriteAsync(Path.Combine(packProject, "device/manifest.json"), manifest);
                var targetFile = Path.Combine(packProject, "device", device.OpenOcd.TargetScript);
                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                await File.WriteAllTextAsync(targetFile, script);
                await JsonStore.WriteAsync(Path.Combine(cubeProject, ".studiox/project.json"), project with
                {
                    Kind = ProjectKind.CubeMx, CubeMx = new("test.ioc", "cmake/gcc-arm-none-eabi.cmake", null)
                });
                var packConfig = (await downloads.ConfigurationAsync(packProject))!;
                var cubeConfig = (await downloads.ConfigurationAsync(cubeProject))!;
                Check(cubeConfig.TargetScriptText == script, "CubeMX exact per-device target script " + device.Id);
                foreach (var template in device.Templates)
                {
                    var resolved = TemplateResolver.Resolve(device, template.Id);
                    Check(Stm32DebugTarget.Find(resolved)?.Core == expectedCore, "Template CPU " + device.Id + "/" + template.Id);
                    templates++;
                }
                foreach (var (directory, config, kind) in new[] { (packProject, packConfig, "pack"), (cubeProject, cubeConfig, "cubemx") })
                foreach (var probe in new[] { "stlink", "cmsis-dap" })
                {
                    var selected = config with { Options = new(probe, 2000) };
                    var target = OpenOcdDebugPlanner.ResolveTarget(selected);
                    Check(target.Core == expectedCore && target.HasFpu == (expectedCore == "Cortex-M4"), "Correct CPU/FPU " + device.Id);
                    var plan = OpenOcdDebugPlanner.Create(directory, selected, tools, Path.Combine(directory, "firmware.elf"), 43333);
                    plans++;
                    Check(plan.OpenOcdArguments.Contains(Path.Combine(tools.ResourceDirectory("openocdScripts"), "interface", probe + ".cfg")), "Probe selection");
                    Check(plan.OpenOcdArguments.Contains("$_TARGETNAME configure -work-area-size 0 -work-area-backup 1"), "RAM preservation");
                    Check(plan.InitializeCommands.Contains("-gdb-set remote hardware-breakpoint-limit unlimited"), "No fixed F407 breakpoint capacity");
                    Check(plan.InitializeCommands.Any(c => c.Contains("monitor studiox_check_target", StringComparison.Ordinal)), "Identity check before debugging");
                    Check(plan.InitializeCommands.Any(c => c.Contains("monitor verify_image", StringComparison.Ordinal)), "ELF verification");
                    Check(!plan.InitializeCommands.Any(c => c.Contains("target-download", StringComparison.Ordinal)), "No implicit download");
                    // noinit 阻止自动连接；只使用假的读数执行身份校验，然后在配置阶段 shutdown。
                    var id = Regex.Match(script, @"\$id != (0x[0-9a-f]+)").Groups[1].Value;
                    var kb = Regex.Match(script, @"\$kb != ([0-9]+)").Groups[1].Value;
                    Check(id.Length > 0 && kb == (device.FlashBytes / 1024).ToString(), "Chip identity / Flash capacity guard");
                    var check = $"set fake_id {id}; set fake_kb {kb}; " +
                        "proc read_memory {address width count} { global fake_id fake_kb; if {$width == 32} {return [list $fake_id]}; return [list $fake_kb] }; " +
                        "studiox_check_target; set fake_id 0; if {![catch {studiox_check_target}]} {error ID_GUARD_FAILED}; " +
                        $"set fake_id {id}; set fake_kb 0; if {{![catch {{studiox_check_target}}]}} {{error SIZE_GUARD_FAILED}}; " +
                        "echo STUDIOX_OFFLINE_OK; shutdown";
                    var parsed = await runner.RunAsync(new(tools.Tool("openocd"), ["-c", "noinit", .. plan.OpenOcdArguments, "-c", check], directory,
                        TimeSpan.FromSeconds(15), environment, RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
                    var log = parsed.StandardOutput + parsed.StandardError;
                    await File.WriteAllTextAsync(Path.Combine(directory, probe + "-parse.log"), log);
                    Check(parsed.Success && log.Contains("STUDIOX_OFFLINE_OK", StringComparison.Ordinal), "Offline OpenOCD parse: " + directory + "/" + probe + "\n" + log);
                    parses++;
                    rows.Add(new { device = device.Id, kind, probe, target.Core, target.HasFpu, success = true });
                }
                reference ??= packConfig;
            }
            packs++;
            Console.WriteLine($"PASS {manifest.Id}: {manifest.Devices.Count} models, four templates, two project kinds, ST-Link + DAP, identity/size guards; noinit");
        }
        Check(packs == 23 && models.Count == 244 && templates == 976 && plans == 976 && parses == 976, "Pinned F1/F4 matrix completeness");
        foreach (var id in new[] { "STM32F103C8T6", "stm32f407zgt6", "STM32F429ZIT6TR" })
        {
            var configDirectory = Path.Combine(root, "cubemx", id[..11].ToUpperInvariant());
            var project = await ProjectService.ReadAsync(configDirectory);
            await JsonStore.WriteAsync(Path.Combine(configDirectory, ".studiox/project.json"), project with { DeviceId = id });
            var config = (await downloads.ConfigurationAsync(configDirectory))!;
            Check(OpenOcdDebugPlanner.ResolveTarget(config).DeviceId == id[..11].ToUpperInvariant(), "Full ordering suffix " + id);
        }
        var valid = reference!;
        foreach (var bad in new[] { "STM32F407FAKE", "STM32F999ZG", "STM32H743ZI", "STM32F103C(E-G)", "AG32VF303CCT6" })
            Check(Stm32DebugTarget.Find(valid.Device with { Id = bad }) is null, "Reject unknown/ambiguous model " + bad);
        Check(Stm32DebugTarget.Find(valid.Device with { Architecture = "riscv" }) is null, "Reject wrong architecture");
        Check(Stm32DebugTarget.Find(valid.Device with { FlashBytes = valid.Device.FlashBytes + 1024 }) is null, "Reject wrong Flash layout");
        Check(Stm32DebugTarget.Find(valid.Device with { RamOrigin = 0 }) is null, "Reject wrong RAM layout");
        var gdb = await runner.RunAsync(new(tools.Tool("gdb"), ["--nx", "--batch", "-ex", "set remote hardware-breakpoint-limit unlimited", "-ex", "show remote hardware-breakpoint-limit"], root,
            TimeSpan.FromSeconds(15), environment, RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
        Check(gdb.Success && gdb.StandardOutput.Contains("unlimited", StringComparison.Ordinal), "Bundled GDB accepts target-managed capacity");
        await File.WriteAllTextAsync(Path.Combine(root, "gdb-capacity.log"), gdb.StandardOutput + gdb.StandardError);
        await RegisterChecks.RunAsync();
        await JsonStore.WriteAsync(Path.Combine(root, "matrix.json"), rows);
        var result = $"PASS: {packs} packages / {models.Count} devices / {templates} templates / {plans} session plans / {parses} OpenOCD noinit parses; M3/M4 register mapping, suffixes, invalid targets and bundled GDB command. No USB connection or hardware execution.";
        await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), result);
        Console.WriteLine(result);
        return 0;
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
