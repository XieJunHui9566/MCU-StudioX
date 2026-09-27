using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Devices;
using StudioX.Engine;
using StudioX.Foundation;

// 本工具只使用文件和假进程；即使传入真实 SDK 工程，也不会启动 esptool 或访问硬件。
if (args.Length is not (1 or 3))
{
    Console.Error.WriteLine("Usage: StudioX.EspressifFlashValidation <new-output> [runtime project]");
    return 2;
}
var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output))
{
    throw new InvalidOperationException("Use a new output directory.");
}
Directory.CreateDirectory(output);
var fixture = Path.Combine(output, "layout fixture with spaces");
Directory.CreateDirectory(Path.Combine(fixture, ".build", "bootloader"));
await File.WriteAllBytesAsync(Path.Combine(fixture, ".build", "bootloader", "bootloader.bin"), new byte[127]);
await File.WriteAllBytesAsync(Path.Combine(fixture, ".build", "app.bin"), new byte[513]);
var passed = new List<string>();
void Check(bool condition, string label)
{
    if (!condition)
    {
        throw new InvalidOperationException(label);
    }
    passed.Add(label);
    Console.WriteLine("PASS " + label);
}
async Task Reject(Func<Task> operation, string label, string? code = null)
{
    try
    {
        await operation();
        throw new InvalidOperationException("Expected rejection: " + label);
    }
    catch (StudioXException ex) when (code is null || ex.Code == code)
    {
        Check(true, label);
    }
}
JsonObject LayoutJson(string target) => new()
{
    ["write_flash_args"] = new JsonArray("--flash_mode", "dio", "--flash_size", "4MB", "--flash_freq", "40m"),
    ["flash_settings"] = new JsonObject { ["flash_mode"] = "dio", ["flash_size"] = "4MB", ["flash_freq"] = "40m" },
    ["flash_files"] = new JsonObject { ["0x1000"] = "bootloader/bootloader.bin", ["0x10000"] = "app.bin" },
    ["app"] = new JsonObject { ["offset"] = "0x10000", ["file"] = "app.bin", ["encrypted"] = false },
    ["extra_esptool_args"] = target == "esp8266"
        ? new JsonObject { ["before"] = "default_reset", ["after"] = "hard_reset" }
        : new JsonObject { ["chip"] = target, ["stub"] = true, ["before"] = "default_reset", ["after"] = "hard_reset" }
};
async Task WriteLayout(JsonObject json) => await File.WriteAllTextAsync(Path.Combine(fixture, ".build", "flasher_args.json"), json.ToJsonString());
Task<EspressifFlashLayout> ReadLayout(string target = "esp32c3", ulong capacity = 0) =>
    EspressifFlashLayoutReader.ParseAsync(fixture, ".build/flasher_args.json", target, capacity);

foreach (var target in new[] { "esp32", "esp32p4", "esp32s3", "esp32c3", "esp32c5", "esp32c6", "esp8266" })
{
    await WriteLayout(LayoutJson(target));
    var parsed = await ReadLayout(target);
    Check(parsed.Target == target && parsed.Images.Length == 2 && parsed.Images[1].Offset == 0x10000 && parsed.UseStub,
        "native SDK layout " + target);
}
await WriteLayout(LayoutJson("esp32c3"));
var layout = await ReadLayout();
Check(layout.Images.All(image => image.Sha256.Length == 64) && layout.LayoutSha256.Length == 64 && layout.ImageBytes == 640,
    "all images and complete layout have independent SHA-256");
var settings = new EspressifFlashSettings(Port: "COM12");
var paths = layout.Images.Select(image => Path.Combine(output, "image " + image.Offset + ".bin")).ToArray();
var writeArguments = EspressifFlashService.CreateArguments(layout, settings, paths, verify: false);
var verifyArguments = EspressifFlashService.CreateArguments(layout, settings, paths, verify: true);
Check(writeArguments.Contains("write_flash") && verifyArguments.Contains("verify_flash") &&
    writeArguments.Contains("COM12") && writeArguments.Contains("esp32c3") &&
    paths.All(path => writeArguments.Contains(path)) && writeArguments.Contains("-B") && writeArguments.Contains("-s"),
    "fixed target, selected port, isolated image paths and Python cache policy");
Check(!writeArguments.Any(argument => argument.Contains("erase", StringComparison.OrdinalIgnoreCase) ||
    argument.Contains("efuse", StringComparison.OrdinalIgnoreCase) || argument == "--force" || argument == "flash"),
    "command exposes no all-chip erase, eFuse, force or idf.py flash");
await Reject(() => Task.Run(() => EspressifFlashService.CreateArguments(layout, new(), paths, false)),
    "no automatic COM selection", "ESP_FLASH_SETTINGS");
await Reject(() => Task.Run(() => new EspressifFlashSettings(Port: "COM1 --force").Validate(true)),
    "COM argument injection rejected", "ESP_FLASH_SETTINGS");
string Evidence(EspressifFlashLayout value) => string.Join("\n", value.Images.Select(image =>
    $"Verifying 0x{((image.Bytes + 3) & ~3L):x} ({((image.Bytes + 3) & ~3L)}) bytes @ 0x{image.Offset:x8} in flash against image.bin...\n-- verify OK (digest matched)"));
Check(EspressifFlashService.HasVerificationEvidence(Evidence(layout), layout), "digest evidence covers each offset and padded byte count");
Check(!EspressifFlashService.HasVerificationEvidence("-- verify OK (digest matched)", layout), "exit code or unbound success line is insufficient");
Check(!EspressifFlashService.HasVerificationEvidence(Evidence(layout).Replace("(516)", "(512)", StringComparison.Ordinal), layout), "wrong verified image size rejected");
Check(!EspressifFlashService.HasVerificationEvidence(Evidence(layout) + "\nverify FAILED", layout), "verification failure cannot be masked by earlier success");
Check(!EspressifFlashService.HasVerificationEvidence(Evidence(layout).Split("Verifying")[1], layout), "partial image verification rejected");
var crossedEvidence = "Verifying 0x80 (128) bytes @ 0x00001000 in flash against bootloader.bin...\n" +
    "Verifying 0x4 (4) bytes @ 0x00010000 in flash against wrong.bin...\n-- verify OK (digest matched)\n" +
    "Verifying 0x204 (516) bytes @ 0x00010000 in flash against app.bin...\n-- verify OK (digest matched)\n";
Check(!EspressifFlashService.HasVerificationEvidence(crossedEvidence, layout), "invalid verification block cannot reuse previous image binding");

async Task RejectMutation(Action<JsonObject> mutation, string label)
{
    var json = LayoutJson("esp32c3");
    mutation(json);
    await WriteLayout(json);
    await Reject(async () => { _ = await ReadLayout(); }, label);
}
await RejectMutation(json => json["extra_esptool_args"]!["chip"] = "esp32c6", "target mismatch rejected");
await RejectMutation(json => ((JsonObject)json["extra_esptool_args"]!).Remove("chip"), "non-8266 missing chip rejected");
await RejectMutation(json => ((JsonObject)json["extra_esptool_args"]!).Remove("stub"), "non-8266 missing stub rejected");
await RejectMutation(json => json["flash_files"]!["0x10000"] = "../../other.bin", "parent escape rejected");
await RejectMutation(json => json["flash_files"]!["0x10000"] = "C:/other.bin", "absolute image path rejected");
await RejectMutation(json => json["flash_files"]!["0x10000"] = "bootloader/bootloader.bin", "duplicate image path rejected");
await RejectMutation(json =>
{
    ((JsonObject)json["flash_files"]!).Remove("0x10000");
    json["flash_files"]!["0x1080"] = "app.bin";
}, "shared erase sector rejected");
await RejectMutation(json =>
{
    ((JsonObject)json["flash_files"]!).Remove("0x10000");
    json["flash_files"]!["0x400000"] = "app.bin";
}, "flash capacity overflow rejected");
await RejectMutation(json => json["app"]!["encrypted"] = true, "encrypted image rejected");
await RejectMutation(json => json["app"]!["encrypted"] = "true", "string encrypted image rejected");
var stringEncrypted = LayoutJson("esp32c3");
stringEncrypted["app"]!["encrypted"] = "false";
await WriteLayout(stringEncrypted);
Check((await ReadLayout()).Images.Length == 2, "native SDK string false encrypted flag accepted");
await RejectMutation(json => ((JsonArray)json["write_flash_args"]!).Add("--erase-all"), "arbitrary write options rejected");
await RejectMutation(json => json["extra_esptool_args"]!["after"] = "no_reset", "security reset mode rejected");
await WriteLayout(LayoutJson("esp32c3"));
await Reject(async () => { _ = await ReadLayout(capacity: 2 * 1024 * 1024); }, "configured capacity exceeds known module physical flash");
await File.WriteAllTextAsync(Path.Combine(fixture, "sdkconfig"), "CONFIG_SECURE_FLASH_ENC_ENABLED=y\n");
await Reject(async () => { _ = await ReadLayout(); }, "sdkconfig flash encryption rejected");
File.Delete(Path.Combine(fixture, "sdkconfig"));
await File.WriteAllTextAsync(Path.Combine(fixture, "sdkconfig"), "CONFIG_SECURE_BOOT=y\n");
await Reject(async () => { _ = await ReadLayout(); }, "sdkconfig secure boot rejected despite ordinary JSON image");
File.Delete(Path.Combine(fixture, "sdkconfig"));
Directory.CreateDirectory(Path.Combine(fixture, ".build", "bootloader", "config"));
var bootloaderConfig = Path.Combine(fixture, ".build", "bootloader", "config", "sdkconfig.h");
await File.WriteAllTextAsync(bootloaderConfig, "#define CONFIG_SECURE_BOOT_V1_ENABLED 1\n");
await Reject(async () => { _ = await ReadLayout(); }, "generated bootloader secure boot rejected without root sdkconfig flag");
await File.WriteAllTextAsync(bootloaderConfig, "#define CONFIG_SECURE_BOOT_V2_RSA_SUPPORTED 1\n");
var actualConfig = Path.Combine(fixture, "actual-sdkconfig");
var descriptionPath = Path.Combine(fixture, ".build", "project_description.json");
await File.WriteAllTextAsync(actualConfig, "CONFIG_SECURE_FLASH_ENC_ENABLED=y\n");
await File.WriteAllTextAsync(descriptionPath, new JsonObject { ["config_file"] = actualConfig }.ToJsonString());
await Reject(async () => { _ = await ReadLayout(); }, "redirected actual SDK config encryption rejected");
await File.WriteAllTextAsync(actualConfig, "# CONFIG_SECURE_BOOT is not set\nCONFIG_SECURE_BOOT_V2_RSA_SUPPORTED=y\n");
Check((await ReadLayout()).Images.Length == 2, "disabled security and supported capability remain ordinary firmware");
File.Delete(descriptionPath);
File.Delete(actualConfig);
File.Delete(bootloaderConfig);
var repeated = LayoutJson("esp32c3").ToJsonString().Replace("\"chip\":\"esp32c3\"", "\"chip\":\"esp32c3\",\"chip\":\"esp32c6\"", StringComparison.Ordinal);
await File.WriteAllTextAsync(Path.Combine(fixture, ".build", "flasher_args.json"), repeated);
await Reject(async () => { _ = await ReadLayout(); }, "duplicate JSON fields rejected");

await using (var hub = new DeviceHub())
{
    using (await hub.ReserveAsync("serial:COM12"))
    {
        await Reject(async () => { using var lease = await hub.ReserveAsync("serial:com12"); }, "port reservation is case insensitive", "DEVICE_OWNED");
        var transport = new FakeTransport("serial:COM12");
        await Reject(async () => { await using var session = await hub.OpenAsync(transport); }, "serial capture cannot open reserved flash port", "DEVICE_OWNED");
        Check(transport.OpenCount == 0, "reservation refuses capture before hardware open");
    }
    var opened = new FakeTransport("serial:COM12");
    await using (var session = await hub.OpenAsync(opened))
    {
        await Reject(async () => { using var lease = await hub.ReserveAsync("serial:COM12"); }, "flash cannot reserve captured port", "DEVICE_OWNED");
    }
    using var released = await hub.ReserveAsync("serial:COM12");
    Check(opened.OpenCount == 1 && opened.DisposeCount == 1, "serial and flash ownership releases without duplicate handles");
}

if (args.Length == 3)
{
    var runtime = Path.GetFullPath(args[1]);
    var project = Path.GetFullPath(args[2]);
    var catalog = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
    var readOnly = new EspressifFlashService(catalog);
    var preview = await readOnly.PreviewAsync(project, settings);
    var nativeTools = await catalog.ResolveAsync(preview.Configuration.Project.ToolsetId,
        preview.Configuration.Project.ToolsetVersion, preview.Configuration.Project.CompilerId);
    var nativeLayout = preview.Layout;
    var mode = "success";
    var calls = new List<ProcessRequest>();
    async Task<ProcessResult> FakeProcess(ProcessRequest request, CancellationToken token)
    {
        await Task.Yield();
        calls.Add(request);
        if (mode == "cancel")
        {
            throw new OperationCanceledException(token);
        }
        var verify = request.Arguments.Contains("verify_flash");
        var result = mode == "fail" ? new ProcessResult(2, "", "fake chip mismatch", false, false) :
            mode == "timeout" ? new ProcessResult(-1, "", "fake timeout", true, false) :
            new ProcessResult(0, verify ? Evidence(nativeLayout) : "fake write complete", "", false, mode == "truncated");
        request.Output?.Report(result.StandardOutput + result.StandardError);
        return result;
    }
    await using var hub = new DeviceHub();
    var flash = new EspressifFlashService(catalog, FakeProcess);
    var service = new EspressifDownloadService(flash, hub);
    async Task<EspressifFlashReport> RunFake() => await service.DownloadApprovedAsync(project, settings,
        preview.Configuration.Device.Id, nativeLayout.LayoutSha256);
    var report = await RunFake();
    Check(report.Success && calls.Count == 2 && calls[0].Arguments.Contains("write_flash") && calls[1].Arguments.Contains("verify_flash"),
        "fake process executes write followed by independent verification");
    Check(calls.All(request => request.Environment!["PYTHONHOME"] == nativeTools.ResourceDirectory("python-env") &&
        request.Executable == nativeTools.Tool("python") && request.RemoveEnvironment!.Contains("PYTHONPATH")),
        "flash requests bind bundled Python and remove ambient module paths");
    Check(!Directory.EnumerateFiles(Path.GetDirectoryName(report.LogPath)!, "*.bin").Any() && File.Exists(report.LogPath),
        "download retains raw log and removes BIN snapshots");
    foreach (var fault in new[] { "fail", "timeout", "truncated" })
    {
        mode = fault;
        calls.Clear();
        var failed = await RunFake();
        Check(!failed.Success && calls.Count == 1, "fake " + fault + " does not run verification or report success");
    }
    mode = "cancel";
    try
    {
        await RunFake();
        throw new InvalidOperationException("Expected cancellation");
    }
    catch (OperationCanceledException)
    {
        using var reservation = await hub.ReserveAsync("serial:COM12");
        Check(true, "fake cancellation releases port reservation");
    }
    mode = "success";
    calls.Clear();
    await Reject(async () => await service.DownloadApprovedAsync(project, settings, preview.Configuration.Device.Id,
        new string('A', 64)), "changed approval rejected before fake process", "ESP_FLASH_APPROVAL_CHANGED");
    Check(calls.Count == 0, "approval rejection never starts process");
    await using var workbench = new WorkbenchService(runtime, Path.Combine(output, "mcp-data"));
    var deny = new RecordingDenyAuthorizer();
    await using var mcp = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(workbench, project, deny));
    var definitions = await mcp.ListToolsAsync();
    Check(definitions.Single(tool => tool.Name == "firmware_download").ParametersJson.Contains("baudRate", StringComparison.Ordinal) &&
        definitions.Single(tool => tool.Name == "firmware_download_plan").ParametersJson.Contains("port", StringComparison.Ordinal),
        "real MCP discovery exposes ESP COM and baud options");
    var planText = await mcp.CallToolAsync("firmware_download_plan", "{\"port\":\"COM12\",\"baudRate\":460800}");
    using var plan = JsonDocument.Parse(planText);
    if (!plan.RootElement.TryGetProperty("imageSha256", out _))
    {
        throw new InvalidOperationException("ESP MCP plan failed: " + planText);
    }
    Check(plan.RootElement.GetProperty("imageSha256").GetString() == nativeLayout.LayoutSha256 &&
        plan.RootElement.GetProperty("images").GetArrayLength() == nativeLayout.Images.Length &&
        plan.RootElement.GetProperty("probeId").GetString() == "esptool" && deny.Requests.Count == 0,
        "real MCP read-only plan returns the complete native layout without approval");
    var rejected = await mcp.CallToolAsync("firmware_download", JsonSerializer.Serialize(new
    {
        deviceId = preview.Configuration.Device.Id,
        imageSha256 = nativeLayout.LayoutSha256,
        probeId = "esptool",
        speedKhz = 0,
        port = "COM12",
        baudRate = 460800
    }));
    Check(rejected.Contains("MCP_APPROVAL_DENIED", StringComparison.Ordinal) && deny.Requests.Count == 1 &&
        deny.Requests[0].Permission == StudioXMcpPermission.FirmwareDownload &&
        nativeLayout.Images.All(image => deny.Requests[0].Summary.Contains(image.Sha256, StringComparison.Ordinal)),
        "real MCP download requests all image hashes and respects denied authorization before hardware");
    var info = await mcp.CallToolAsync("project_info", "{}");
    var config = await mcp.CallToolAsync("project_read_file", "{\"path\":\"sdkconfig\",\"startLine\":1,\"maxLines\":20}");
    Check(info.Contains("esp32c3", StringComparison.Ordinal) && config.Contains("sdkconfig", StringComparison.Ordinal) &&
        !config.Contains("MCP_PATH", StringComparison.Ordinal), "MCP exposes exact SDK identity and allows bounded sdkconfig read");
    var create = await mcp.CallToolAsync("project_create_file", "{\"path\":\"partitions.csv\",\"content\":\"# Name, Type, SubType, Offset, Size\\n\"}");
    Check(create.Contains("MCP_APPROVAL_DENIED", StringComparison.Ordinal) && deny.Requests.Last().Permission == StudioXMcpPermission.FileWrite,
        "MCP native partition config requires FileWrite authorization");
}
await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
{
    HardwareAccessed = false,
    passed.Count,
    Passed = passed
}, JsonStore.Options));
return 0;

internal sealed class FakeTransport(string key) : IDeviceTransport
{
    public string Key => key;
    public bool IsSimulated => true;
    public int OpenCount
    {
        get; private set;
    }
    public int DisposeCount
    {
        get; private set;
    }
    public ValueTask OpenAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        OpenCount++;
        return ValueTask.CompletedTask;
    }
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        yield break;
    }
    public ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingDenyAuthorizer : IStudioXMcpAuthorizer
{
    public List<StudioXMcpApprovalRequest> Requests { get; } = [];
    public Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token)
    {
        Requests.Add(request);
        return Task.FromResult(false);
    }
}
