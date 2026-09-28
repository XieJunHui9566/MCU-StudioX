using System.Security.Cryptography;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;
using StudioX.Packages;

if (args is ["--connection-recovery", var sourceProject, var validationOutput])
{
    return await ConnectionRecoveryChecks.RunAsync(sourceProject, validationOutput);
}
if (args is ["--preview-existing", var existingProject, var existingRuntime, var previewOutput])
{
    return await ExistingProjectChecks.RunAsync(existingProject, existingRuntime, previewOutput);
}
if (args is ["--build-existing", var buildProject, var buildRuntime, var buildOutput])
{
    var build = await new BuildService(new ToolsetCatalog(Path.Combine(Path.GetFullPath(buildRuntime), "toolsets"))).BuildAsync(buildProject);
    await File.WriteAllTextAsync(Path.GetFullPath(buildOutput) + ".build.log", build.Log);
    if (!build.Success) { throw new InvalidOperationException("Existing project build failed: " + build.LogPath); }
    return await ExistingProjectChecks.RunAsync(buildProject, buildRuntime, buildOutput);
}

// 本程序只生成工程、真实离线编译和准备命令；所有拒绝测试必须发生在硬件进程启动之前。
if (args.Length != 3) { throw new ArgumentException("Usage: <AG32 mcupack> <runtime directory> <new output directory>"); }
var archive = Path.GetFullPath(args[0]);
var runtime = Path.GetFullPath(args[1]);
var output = Path.GetFullPath(args[2]);
if (Directory.Exists(output)) { throw new InvalidOperationException("Choose a new output directory."); }
Directory.CreateDirectory(output);
var results = new List<string>();
var tools = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
var downloads = new OpenOcdService(tools);
var builds = new BuildService(tools);
var pack = await new PackRepository(Path.Combine(output, "repository")).ImportAsync(archive);
var project = Path.Combine(output, "中文 映射工程");
await new ProjectService().CreateAsync(pack, "AG32VF303CCT6", "minimal", "mapping_test", project);
var pinMap = Path.Combine(project, "logic", "pins.ve");
await File.WriteAllTextAsync(pinMap, "SYSCLK 200\nBUSCLK 100\nHSECLK 8\nGPIO4_4 PIN_21\n");
var report = await builds.BuildAsync(project);
await File.WriteAllTextAsync(Path.Combine(output, "first-build.log"), report.Log);
Check(report.Success, "MCU and PIN_21 mapping compile successfully; original diagnostics saved in first-build.log");
CheckPlacedPin("PIN_21");
var configuration = await downloads.ConfigurationAsync(project) ?? throw new InvalidOperationException("Missing download configuration");
var first = await downloads.PreviewAsync(project, configuration.Options);
Check(first.Images.Count == 2 && first.Images[0].Role == "application" && first.Images[1].Role == "pin-mapping" &&
    first.Images[0].Address == 0x80000000 && first.Images[1].Address == 0x80027000 &&
    first.ApprovalSha256.Length == 64 && first.ApprovalSha256 != first.Sha256, "approval binds application and mapping contents plus addresses");
var prepared = await downloads.PrepareAsync(project, configuration.Options);
Check(prepared.Images.Count == 2 && prepared.Images.All(image =>
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(image.Path))) == image.Preview.Sha256.ToUpperInvariant()),
    "two independent snapshots preserve exactly the approved images");
var command = prepared.Arguments[^1];
Check(command.IndexOf("studiox_check_target", StringComparison.Ordinal) < command.IndexOf("flash write_image", StringComparison.Ordinal) &&
    command.Contains("0x80027000 bin", StringComparison.Ordinal) && command.Contains("reset run", StringComparison.Ordinal) &&
    !command.Contains("write_fpga_config", StringComparison.Ordinal) && !command.Contains("options_write", StringComparison.Ordinal) &&
    !command.Contains("mass_erase", StringComparison.Ordinal), "download checks target first, writes only the fixed images, preserves options and restarts");
var hardwarePreparation = await HardwareDebugPreparer.PrepareAsync(project, downloads);
Check(hardwarePreparation.PinMapping is not null && File.Exists(hardwarePreparation.PinMapping.Path),
    "debug preparation includes the exact mapping snapshot for read-only verification");
Check(hardwarePreparation.Configuration.TargetScriptText == Ag32PinMappingTargetScript.RequireCompatible(project, configuration.OpenOcd.TargetScript) &&
    prepared.Arguments.Contains(hardwarePreparation.Configuration.TargetScriptText),
    "download and debug execute embedded identity, protection and logic-layout guards");
var targetFile = Path.Combine(project, "device", configuration.OpenOcd.TargetScript);
var targetText = await File.ReadAllTextAsync(targetFile);
await File.WriteAllTextAsync(targetFile, targetText + "\nproc studiox_check_target {} {}\n");
await Reject(() => downloads.PreviewAsync(project, configuration.Options), "edited target guards are rejected before hardware access");
await File.WriteAllTextAsync(targetFile, targetText);
var evidence = "STUDIOX_VERIFY_APPLICATION_BEGIN\nverified " + prepared.Images[0].VerificationBytes +
    " bytes in 0.1s\nSTUDIOX_VERIFY_APPLICATION_END\nSTUDIOX_VERIFY_PIN_MAPPING_BEGIN\nverified " +
    prepared.Images[1].VerificationBytes + " bytes in 0.1s\nSTUDIOX_VERIFY_PIN_MAPPING_END\n";
Ag32PinMappingDownloadEvidence.Require(evidence, prepared.Images, "fixture.log");
Check(true, "both images require separate full byte-count evidence");
await Reject(() => Task.Run(() => Ag32PinMappingDownloadEvidence.Require(
    evidence.Replace("STUDIOX_VERIFY_PIN_MAPPING_END", "missing", StringComparison.Ordinal), prepared.Images, "fixture.log")),
    "missing mapping verification cannot count as success");
await Reject(() => Task.Run(() => Ag32PinMappingDownloadEvidence.Require(
    evidence + "Error: error reading USB data\n", prepared.Images, "fixture.log")), "transport failures outside image boundaries are still rejected");
await Reject(() => Task.Run(() => Ag32PinMappingDownloadEvidence.Require(evidence, prepared.Images, "fixture.log", true)),
    "truncated evidence is rejected");
await File.WriteAllTextAsync(pinMap, "SYSCLK 200\nBUSCLK 100\nHSECLK 8\nGPIO4_4 PIN_2\n");
await Reject(() => downloads.PreviewAsync(project, configuration.Options), "edited VE invalidates the old mapping immediately");
report = await builds.BuildAsync(project);
await File.WriteAllTextAsync(Path.Combine(output, "second-build.log"), report.Log);
Check(report.Success, "MCU and PIN_2 mapping compile successfully; original diagnostics saved in second-build.log");
CheckPlacedPin("PIN_2");
var second = await downloads.PreviewAsync(project, configuration.Options);
Check(second.Sha256 == first.Sha256 && second.Images[1].Sha256 != first.Images[1].Sha256 &&
    second.ApprovalSha256 != first.ApprovalSha256, "moving GPIO4_4 from PIN_21 to PIN_2 changes real mapping bytes and approval while MCU code stays identical");
await Reject(() => downloads.DownloadApprovedAsync(project, configuration.Options, "AG32VF303CCT6", first.ApprovalSha256),
    "previous layout approval is refused before any hardware session starts");
var changedAddress = second.Images.Select(image => image.Role == "pin-mapping" ? image with { Address = 0x80026000 } : image).ToArray();
Check(DownloadImageLayout.ApprovalSha256(changedAddress) != second.ApprovalSha256, "changing a flash address changes the approval hash");
var finalPrepared = await downloads.PrepareAsync(project, configuration.Options);
await Reject(() => Task.Run(() => OpenOcdService.CreatePinMappingArguments(project, configuration, configuration.Options,
    finalPrepared.Tools, finalPrepared.Images.Select(image => image.Preview.Role == "pin-mapping"
        ? image with { Preview = image.Preview with { Address = 0x80026000 } } : image).ToArray())), "mapping cannot overlap MCU application space");
var mappingFile = Path.Combine(project, second.Images[1].RelativePath.Replace('/', Path.DirectorySeparatorChar));
await File.AppendAllTextAsync(mappingFile, "tamper");
await Reject(() => downloads.PreviewAsync(project, configuration.Options), "modified mapping binaries cannot be downloaded");
File.Copy(finalPrepared.Images.Single(image => image.Preview.Role == "pin-mapping").Path, mappingFile, true);
Check((await downloads.PreviewAsync(project, configuration.Options)).ApprovalSha256 == second.ApprovalSha256,
    "restored exact snapshot leaves a valid fixture for UI and external MCP verification");
await File.WriteAllLinesAsync(Path.Combine(output, "result.txt"), results.Prepend("PASS — offline only; no hardware connected, no flash or option bytes written"));
return 0;

void CheckPlacedPin(string pin)
{
    var io = File.ReadAllLines(Path.Combine(project, ".build", "ag32-mapping", "logic_db", "io.asf"));
    Check(io.Count(line => line.Trim() == "set_location_assignment -to GPIO4_4 " + pin) == 1,
        "actual Supra placement binds GPIO4_4 to " + pin);
}

void Check(bool condition, string description)
{
    if (!condition) { throw new InvalidOperationException(description); }
    results.Add(description);
    Console.WriteLine("PASS " + description);
}

async Task Reject(Func<Task> action, string description)
{
    try { await action(); }
    catch (StudioXException) { Check(true, description); return; }
    throw new InvalidOperationException("Expected rejection: " + description);
}
