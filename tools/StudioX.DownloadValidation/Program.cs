using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;
using StudioX.Packages;

// 全部测试只预检或准备固件；OpenOCD 只解析配置并执行模拟读数，绝不 init。
var nativeF407Only = args is ["--native-f407", _, _, _];
string runtime, output, cubeF1, cubeF4, packs;
if (nativeF407Only)
{
    runtime = args[1]; output = args[2]; cubeF1 = cubeF4 = "";
    packs = Path.GetDirectoryName(args[3])!;
}
else if (args is [var runtimeArg, var outputArg, var cubeF1Arg, var cubeF4Arg, var packsArg])
{
    runtime = runtimeArg; output = outputArg; cubeF1 = cubeF1Arg; cubeF4 = cubeF4Arg; packs = packsArg;
}
else return 2;
var root = Path.GetFullPath(output);
if (Directory.Exists(root)) throw new InvalidOperationException("Use a new output directory.");
Directory.CreateDirectory(root);
var catalog = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
var downloads = new OpenOcdService(catalog);
var builds = new BuildService(catalog);
var passed = new List<string>();
void Pass(string text) { passed.Add(text); Console.WriteLine("PASS " + text); }
void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); }
async Task Reject(Func<Task> action, string code)
{
    try { await action(); throw new InvalidOperationException("Expected rejection: " + code); }
    catch (StudioXException ex) when (ex.Code == code) { Pass("reject " + code); }
}

if (nativeF407Only)
{
    var nativeRepository = new PackRepository(Path.Combine(root, "packs"));
    var pack = await nativeRepository.ImportAsync(args[3]);
    var project = Path.Combine(root, "native-f407");
    await new ProjectService().CreateAsync(pack, "STM32F407ZG", "hal-freertos", "mcp_download_test", project);
    await BuildAndPrepare(project, false);
    await File.WriteAllLinesAsync(Path.Combine(root, "result.txt"), passed.Prepend("PASS — offline only; no hardware accessed"));
    return 0;
}

foreach (var (source, name) in new[] { (cubeF1, "cube-f103"), (cubeF4, "cube-f407") })
{
    var project = Path.Combine(root, name + " with spaces");
    CopyFixture(source, project);
    await BuildAndPrepare(project, true);
    if (name == "cube-f103") await ImageBoundaries(project);
}
var repository = new PackRepository(Path.Combine(root, "packs"));
foreach (var (family, device, template) in new[] { ("f103", "STM32F103C8", "spl"), ("f407", "STM32F407ZG", "hal-freertos") })
{
    var pack = await repository.ImportAsync(Path.Combine(packs, $"STM32-0.1.1/studiox.stm32{family}-0.1.1.mcupack"));
    var project = Path.Combine(root, "native-" + family);
    await new ProjectService().CreateAsync(pack, device, template, "download_test", project);
    await BuildAndPrepare(project, false);
}
var agPack = await repository.ImportAsync(Path.Combine(packs, "studiox.preview.ag32vf303-0.1.1.mcupack"));
var agRoot = Path.Combine(root, "native-ag32");
await new ProjectService().CreateAsync(agPack, "AG32VF303CCT6", "minimal", "ag_download_test", agRoot);
var agConfig = await downloads.ConfigurationAsync(agRoot) ?? throw new InvalidOperationException("Missing AG32 download configuration");
Check(agConfig.OpenOcd.Probes.Select(p => p.Id).SequenceEqual(["agm-blaster"]) &&
    agConfig.OpenOcd.ApplicationFlashBytes == 0x27000 && agConfig.Options.ProbeId == "agm-blaster",
    "AG32 uses only the official probe and 156 KiB application region");
await Reject(() => downloads.PrepareAsync(agRoot, new("stlink", 2000)), "DOWNLOAD_PROBE");
await BuildAndPrepare(agRoot, false);
await File.WriteAllLinesAsync(Path.Combine(root, "result.txt"), passed.Prepend("PASS — offline only; no hardware accessed"));
return 0;

async Task BuildAndPrepare(string project, bool cube)
{
    var config = await downloads.ConfigurationAsync(project) ?? throw new InvalidOperationException("Missing configuration");
    if (config.Device.Architecture == "arm") Check(config.OpenOcd.Probes.Select(p => p.Id).SequenceEqual(["stlink", "cmsis-dap", "jlink"]), "Expected 3 probes");
    await Reject(() => downloads.PrepareAsync(project, config.Options), "DOWNLOAD_BUILD");
    var built = await builds.BuildAsync(project);
    await File.WriteAllTextAsync(Path.Combine(project, "validation-build.log"), built.Log);
    Check(built.Success, built.Log);
    var snapshotsBeforePreview = Directory.EnumerateDirectories(Path.Combine(project, ".build"), "download-*").Count();
    var preview = await downloads.PreviewAsync(project, config.Options);
    Check(preview.Configuration.Device.Id == config.Device.Id && preview.Sha256.Length == 64 &&
        File.Exists(preview.SourceImage) &&
        Directory.EnumerateDirectories(Path.Combine(project, ".build"), "download-*").Count() == snapshotsBeforePreview,
        "Read-only download preview validates current firmware without creating snapshots");
    if (!cube)
    {
        var sourceFile = Path.Combine(project, "src", "main.c");
        var originalSource = await File.ReadAllTextAsync(sourceFile);
        try
        {
            await File.AppendAllTextAsync(sourceFile, "\n/* stale build preview check */\n");
            await Reject(() => downloads.PreviewAsync(project, config.Options), "DOWNLOAD_BUILD");
        }
        finally { await File.WriteAllTextAsync(sourceFile, originalSource); }
    }
    await Reject(() => downloads.DownloadApprovedAsync(project, config.Options,
        config.Device.Id, new string('0', 64)), "DOWNLOAD_APPROVAL_CHANGED");
    await Reject(() => downloads.DownloadApprovedAsync(project, config.Options,
        "WRONG_DEVICE", preview.Sha256), "DOWNLOAD_APPROVAL_CHANGED");
    Check(Directory.EnumerateDirectories(Path.Combine(project, ".build"), "download-*").Count() == snapshotsBeforePreview,
        "Wrong approved hash rejects before snapshot or hardware access");
    foreach (var probe in config.OpenOcd.Probes)
    {
        var options = new DownloadOptions(probe.Id, 1500, "offline-serial [literal] $value");
        await downloads.SaveOptionsAsync(project, options);
        Check((await downloads.ConfigurationAsync(project))!.Options == options, "Settings roundtrip");
        var plan = await downloads.PrepareAsync(project, options);
        if (probe.Id is "stlink" or "cmsis-dap")
            Check((await HardwareDebugPreparer.PrepareAsync(project, downloads)).Elf.EndsWith(".elf", StringComparison.Ordinal), "Debug ELF architecture and Flash ranges");
        Check(plan.Image.EndsWith(cube ? ".elf" : ".bin", StringComparison.Ordinal), "Image format");
        var command = plan.Arguments[^1];
        Check(command.IndexOf("studiox_check_target", StringComparison.Ordinal) < command.IndexOf("flash write_image", StringComparison.Ordinal), "Guard must precede erase");
        Check(command.Contains(cube ? " 0 elf]" : " bin]", StringComparison.Ordinal), "ELF address must not be rebased");
        Check(!command.Contains("unlock", StringComparison.Ordinal) && !command.Contains("mass_erase", StringComparison.Ordinal), "No unlock or mass erase");
        var script = config.TargetScriptText ?? await File.ReadAllTextAsync(Path.Combine(project, "device", config.OpenOcd.TargetScript));
        var expectedId = Regex.Match(script, @"\$id != (0x[0-9a-f]+)").Groups[1].Value;
        var expectedKb = Regex.Match(script, @"\$kb != ([0-9]+)").Groups[1].Value;
        string mock;
        if (config.Device.Architecture == "arm")
            mock = $"set fake_id {expectedId}; set fake_kb {expectedKb}; " +
                "proc read_memory {address width count} { global fake_id fake_kb; if {$width == 32} {return [list $fake_id]}; return [list $fake_kb] }; " +
                "studiox_check_target; set fake_id 0; if {![catch {studiox_check_target}]} {error ID_GUARD_FAILED}; " +
                $"set fake_id {expectedId}; set fake_kb 0; if {{![catch {{studiox_check_target}}]}} {{error SIZE_GUARD_FAILED}}; echo STUDIOX_OFFLINE_OK; shutdown";
        else mock = "echo STUDIOX_OFFLINE_OK; shutdown";
        var dry = plan.Arguments[..^2].Concat(["-c", mock]).ToArray();
        var parsed = await new ProcessRunner().RunAsync(new(plan.Tools.Tool("openocd"), dry, project, TimeSpan.FromSeconds(20),
            ToolsetEnvironment.Create(plan.Tools), RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
        await File.WriteAllTextAsync(Path.Combine(project, probe.Id + "-offline.log"), parsed.StandardOutput + parsed.StandardError);
        Check(parsed.Success && (parsed.StandardOutput + parsed.StandardError).Contains("STUDIOX_OFFLINE_OK", StringComparison.Ordinal), parsed.StandardOutput + parsed.StandardError);
        Pass($"{config.Device.Id} / {probe.DisplayName}: build, snapshot, arguments, Tcl guard, settings persistence");
    }
    await downloads.SaveOptionsAsync(project, config.Options);
    await Reject(() => downloads.SaveOptionsAsync(project, new(config.Options.ProbeId, 0)), "DOWNLOAD_OPTIONS");
}

async Task ImageBoundaries(string project)
{
    var config = (await downloads.ConfigurationAsync(project))!;
    var plan = await downloads.PrepareAsync(project, config.Options);
    var receiptPath = Path.Combine(project, ".build/studiox-build-receipt.json");
    var receiptText = await File.ReadAllTextAsync(receiptPath);
    var original = await File.ReadAllBytesAsync(plan.SourceImage);
    async Task ReplaceImage(byte[] bytes, bool hash)
    {
        await File.WriteAllBytesAsync(plan.SourceImage, bytes);
        if (hash)
        {
            var receipt = JsonNode.Parse(receiptText)!;
            receipt["images"]![0]!["sha256"] = Convert.ToHexString(SHA256.HashData(bytes));
            await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString(JsonStore.Options));
        }
    }
    try
    {
        var altered = original.ToArray(); altered[^1] ^= 1;
        await ReplaceImage(altered, false);
        await Reject(() => downloads.PrepareAsync(project, config.Options), "DOWNLOAD_CHANGED");
        Check((await File.ReadAllBytesAsync(plan.Image)).SequenceEqual(original), "Snapshot changed with original");
        await ReplaceImage([0x7f, 69, 76, 70], true);
        await Reject(() => downloads.PrepareAsync(project, config.Options), "DOWNLOAD_IMAGE");
        var header = (int)BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(28));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(44));
        var loads = Enumerable.Range(0, count).Select(i => header + i * 32)
            .Where(row => BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(row)) == 1 && BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(row + 16)) > 0).ToArray();
        altered = original.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(altered.AsSpan(loads[0] + 12), 0x1ffff800);
        await ReplaceImage(altered, true);
        await Reject(() => downloads.PrepareAsync(project, config.Options), "DOWNLOAD_IMAGE");
        altered = original.ToArray(); altered[18] = 243; // RISC-V ELF cannot be flashed as STM32.
        await ReplaceImage(altered, true);
        await Reject(() => downloads.PrepareAsync(project, config.Options), "DOWNLOAD_IMAGE");
        altered = original.ToArray();
        foreach (var row in loads) BinaryPrimitives.WriteUInt32LittleEndian(altered.AsSpan(row + 12), BinaryPrimitives.ReadUInt32LittleEndian(altered.AsSpan(row + 12)) + 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(altered.AsSpan(24), BinaryPrimitives.ReadUInt32LittleEndian(altered.AsSpan(24)) + 0x1000);
        await ReplaceImage(altered, true);
        var shifted = await downloads.PrepareAsync(project, config.Options);
        Check(shifted.Arguments[^1].Contains(" 0 elf]", StringComparison.Ordinal), "Relocated firmware rebased");
        Pass("ELF flash offsets preserved; initialized data uses Flash LMA; changed files cannot change snapshot");
        var multi = JsonNode.Parse(receiptText)!; multi["images"]!.AsArray().Add(multi["images"]![0]!.DeepClone());
        await File.WriteAllTextAsync(receiptPath, multi.ToJsonString(JsonStore.Options));
        await Reject(() => downloads.PrepareAsync(project, config.Options), "DOWNLOAD_TARGET");
    }
    finally { await File.WriteAllBytesAsync(plan.SourceImage, original); await File.WriteAllTextAsync(receiptPath, receiptText); }
    var manifestPath = Path.Combine(project, ".studiox/project.json");
    var manifest = await ProjectService.ReadAsync(project);
    await JsonStore.WriteAsync(manifestPath, manifest with { DeviceId = "STM32H743ZIT6" });
    Check(await downloads.ConfigurationAsync(project) is null, "Unsupported chip guessed");
    await JsonStore.WriteAsync(manifestPath, manifest with { DeviceId = "STM32F103C(8-B)Tx" });
    Check(await downloads.ConfigurationAsync(project) is null, "Ambiguous capacity guessed");
    await JsonStore.WriteAsync(manifestPath, manifest);
    Pass("Unknown and ambiguous devices stay unsupported");
    var main = Path.Combine(project, "Core/Src/main.c"); var source = await File.ReadAllTextAsync(main);
    try
    {
        await File.AppendAllTextAsync(main, "\n#error Intentional_download_validation_failure\n");
        Check(!(await builds.BuildAsync(project)).Success, "Broken build succeeded");
        await Reject(() => downloads.PrepareAsync(project, config.Options), "DOWNLOAD_BUILD");
    }
    finally { await File.WriteAllTextAsync(main, source); }
    using (var cancelled = new CancellationTokenSource())
    {
        try { await builds.BuildAsync(project, new InlineProgress(_ => cancelled.Cancel()), cancelled.Token); throw new InvalidOperationException("Cancelled build succeeded"); }
        catch (OperationCanceledException) { }
        await Reject(() => downloads.PrepareAsync(project, config.Options), "DOWNLOAD_BUILD");
    }
    Check((await builds.BuildAsync(project)).Success, "Recovery build failed");
    await downloads.PrepareAsync(project, config.Options);
    Pass("Failed and cancelled builds reject stale firmware; recovery rebuild succeeds");
}

static void CopyFixture(string source, string target)
{
    Directory.CreateDirectory(target);
    foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Fixture contains a link");
        if (entry.Name is ".build" or "build" or ".git" or ".studiox") continue;
        var destination = Path.Combine(target, entry.Name);
        if (entry is DirectoryInfo) CopyFixture(entry.FullName, destination); else File.Copy(entry.FullName, destination);
    }
    if (File.Exists(Path.Combine(source, ".studiox/project.json")))
    {
        Directory.CreateDirectory(Path.Combine(target, ".studiox"));
        File.Copy(Path.Combine(source, ".studiox/project.json"), Path.Combine(target, ".studiox/project.json"));
    }
}
sealed class InlineProgress(Action<string> action) : IProgress<string> { public void Report(string value) => action(value); }
