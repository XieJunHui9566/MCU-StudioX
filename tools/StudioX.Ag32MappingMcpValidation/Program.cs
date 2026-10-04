using System.Security.Cryptography;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Engine;
using StudioX.Foundation;

// 所有真实烧录请求由拒绝授权器拦截；只有隔离旧工程的基础映射启用得到允许。
if (args.Length is < 3 or > 4)
{
    throw new ArgumentException("Usage: <runtime> <built isolated AG32 project> <new output> [CLI executable]");
}
var runtime = Path.GetFullPath(args[0]);
var source = Path.GetFullPath(args[1]);
var output = Path.GetFullPath(args[2]);
if (Directory.Exists(output))
{
    throw new InvalidOperationException("Choose a new output directory.");
}
Directory.CreateDirectory(output);
var originalVe = File.ReadAllBytes(PathBoundary.Resolve(source, "logic/pins.ve"));
var originalProject = File.ReadAllBytes(PathBoundary.Resolve(source, ".studiox/project.json"));
var authorizer = new TestAuthorizer();
await using var services = new WorkbenchService(runtime, Path.Combine(output, "data"));
await using var built = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services, source, authorizer, includePlugins: false));
var checks = new List<string>();
var responses = new Dictionary<string, JsonElement>();
void Check(bool value, string name)
{
    if (!value)
    {
        throw new InvalidOperationException(name);
    }
    checks.Add(name);
    Console.WriteLine("PASS " + name);
}
async Task<JsonElement> Call(StudioXMcpSession session, string name, object arguments, string label)
{
    var text = await session.CallToolAsync(name, JsonSerializer.Serialize(arguments));
    using var json = JsonDocument.Parse(text);
    var value = json.RootElement.Clone();
    responses[label] = value;
    return value;
}
var definitions = await built.ListToolsAsync();
Check(new[] { "ag32_pin_mapping_status", "ag32_pin_mapping_enable", "ag32_pin_mapping_build" }
    .All(name => definitions.Count(item => item.Name == name) == 1), "real MCP handshake exposes all three mapping tools exactly once");
var status = await Call(built, "ag32_pin_mapping_status", new { }, "status");
Check(status.GetProperty("Enabled").GetBoolean() && status.GetProperty("ReceiptCurrent").GetBoolean() && authorizer.Requests.Count == 0,
    "read-only mapping status validates current receipt without approval");
var plan = await Call(built, "firmware_download_plan", new { }, "plan");
Check(!plan.TryGetProperty("error", out _) && plan.GetProperty("images").GetArrayLength() == 2,
    "firmware plan contains MCU and VE mapping images");
var images = plan.GetProperty("images").EnumerateArray().ToArray();
Check(images[0].GetProperty("address").GetString() == "0x80000000" && images[1].GetProperty("address").GetString() == "0x80027000" &&
    images.All(image => image.GetProperty("Sha256").GetString()?.Length == 64 && image.GetProperty("Bytes").GetInt64() > 0),
    "plan preserves both explicit addresses sizes and full content hashes");
Check(plan.GetProperty("imageSha256").GetString() != images[0].GetProperty("Sha256").GetString() && authorizer.Requests.Count == 0,
    "approval hash binds complete layout rather than only MCU content");
var downloadArguments = new
{
    deviceId = plan.GetProperty("deviceId").GetString(),
    imageSha256 = plan.GetProperty("imageSha256").GetString(),
    probeId = plan.GetProperty("probeId").GetString(),
    speedKhz = plan.GetProperty("speedKhz").GetInt32()
};
var denied = await Call(built, "firmware_download", downloadArguments, "download-denied");
Check(denied.GetProperty("code").GetString() == "MCP_APPROVAL_DENIED" && authorizer.Requests.Count == 1 &&
    authorizer.Requests[0].Summary.Contains(images[0].GetProperty("Sha256").GetString()!, StringComparison.Ordinal) &&
    authorizer.Requests[0].Summary.Contains(images[1].GetProperty("Sha256").GetString()!, StringComparison.Ordinal) &&
    authorizer.Requests[0].Summary.Contains("0x80027000", StringComparison.Ordinal), "denied burn approval exposes both images and never starts hardware");
var buildDenied = await Call(built, "ag32_pin_mapping_build", new { }, "build-denied");
Check(buildDenied.GetProperty("code").GetString() == "MCP_APPROVAL_DENIED", "mapping compilation requires separate approval");
var legacy = Path.Combine(output, "legacy");
foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
{
    var relative = Path.GetRelativePath(source, path).Replace('\\', '/');
    if (relative.StartsWith(".build/", StringComparison.Ordinal) || relative.StartsWith(".git/", StringComparison.Ordinal) ||
        relative.StartsWith("logic/.pio/", StringComparison.Ordinal) || relative.StartsWith(".pio/", StringComparison.Ordinal))
    {
        continue;
    }
    var destination = PathBoundary.Resolve(legacy, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.Copy(PathBoundary.Resolve(source, relative), destination);
}
var manifestPath = PathBoundary.Resolve(legacy, ".studiox/project.json");
var legacyManifest = await ProjectService.ReadAsync(legacy);
await JsonStore.WriteAsync(manifestPath, legacyManifest with { PinMapping = null, Logic = null });
var vePath = PathBoundary.Resolve(legacy, "logic/pins.ve");
var veHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(vePath)));
var dirty = true;
await using var old = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services, legacy, authorizer,
    () => Task.FromResult(dirty), includePlugins: false));
var beforeRequests = authorizer.Requests.Count;
var unsaved = await Call(old, "ag32_pin_mapping_enable", new { }, "enable-dirty");
Check(unsaved.GetProperty("code").GetString() == "MCP_UNSAVED_FILES" && authorizer.Requests.Count == beforeRequests,
    "unsaved VE blocks enable before approval");
dirty = false;
authorizer.AllowEnable = true;
authorizer.OnApproval = request =>
{
    if (request.Tool == "ag32_pin_mapping_enable")
    {
        File.AppendAllText(vePath, "# approval race fixture\n");
    }
};
var race = await Call(old, "ag32_pin_mapping_enable", new { }, "enable-race");
Check(race.GetProperty("code").GetString() == "AG32_MAPPING_CHANGED" && (await ProjectService.ReadAsync(legacy)).PinMapping is null,
    "VE changing during approval cannot silently enable mapping");
authorizer.OnApproval = null;
await File.WriteAllBytesAsync(vePath, originalVe);
var enabled = await Call(old, "ag32_pin_mapping_enable", new { }, "enable");
Check(enabled.GetProperty("changed").GetBoolean() && (await ProjectService.ReadAsync(legacy)).PinMapping is not null &&
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(vePath))) == veHash,
    "explicit authorized enable preserves original VE bytes");
beforeRequests = authorizer.Requests.Count;
var repeated = await Call(old, "ag32_pin_mapping_enable", new { }, "enable-repeat");
Check(!repeated.GetProperty("changed").GetBoolean() && authorizer.Requests.Count == beforeRequests,
    "already enabled mapping returns immediately without repeated authorization");
File.Delete(vePath);
var restored = await Call(old, "ag32_pin_mapping_enable", new { }, "enable-restore");
Check(restored.GetProperty("changed").GetBoolean() && File.Exists(vePath) &&
    File.ReadAllText(vePath).Contains("GPIO4_4 PIN_21", StringComparison.Ordinal),
    "deleted VE can be explicitly restored even when mapping metadata remains enabled");
await JsonStore.WriteAsync(manifestPath, legacyManifest with { PinMapping = null, Logic = new("AGRV2KL48", "logic/user_logic.v", "logic/pins.ve") });
beforeRequests = authorizer.Requests.Count;
var custom = await Call(old, "ag32_pin_mapping_enable", new { }, "enable-custom");
Check(custom.GetProperty("code").GetString() == "AG32_MAPPING_DEVICE" && authorizer.Requests.Count == beforeRequests,
    "custom Verilog cannot be converted to default mapping");
Check(File.ReadAllBytes(PathBoundary.Resolve(source, "logic/pins.ve")).SequenceEqual(originalVe) &&
    File.ReadAllBytes(PathBoundary.Resolve(source, ".studiox/project.json")).SequenceEqual(originalProject), "original built source project remains unchanged");
if (args.Length == 4)
{
    var cli = Path.GetFullPath(args[3]);
    var transport = new StdioClientTransport(new StdioClientTransportOptions
    {
        Command = cli,
        Arguments = ["mcp", source, runtime, Path.Combine(output, "external-data")],
        Name = "AG32 offline validation",
        EnvironmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables()
    });
    await using var external = await McpClient.CreateAsync(transport);
    var externalDefinitions = await external.ListToolsAsync();
    Check(new[] { "ag32_pin_mapping_status", "ag32_pin_mapping_enable", "ag32_pin_mapping_build" }
        .All(name => externalDefinitions.Count(tool => tool.ProtocolTool.Name == name) == 1),
        "external stdio MCP exposes the same three mapping tools");
    var externalPlan = await external.CallToolAsync("firmware_download_plan", new Dictionary<string, object?>());
    var externalJson = JsonDocument.Parse(string.Join("\n", externalPlan.Content.OfType<TextContentBlock>().Select(block => block.Text)));
    Check(externalPlan.IsError != true && externalJson.RootElement.GetProperty("imageSha256").GetString() == plan.GetProperty("imageSha256").GetString() &&
        externalJson.RootElement.GetProperty("images").GetArrayLength() == 2,
        "external stdio plan binds exactly the same approved two-image layout");
    responses["external-plan"] = externalJson.RootElement.Clone();
    externalJson.Dispose();
}
await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
{
    success = true,
    checks,
    responses,
    hardwareConnected = false,
    logicToolsExecuted = false
}, JsonStore.Options));

internal sealed class TestAuthorizer : IStudioXMcpAuthorizer
{
    public List<StudioXMcpApprovalRequest> Requests { get; } = [];
    public bool AllowEnable
    {
        get; set;
    }
    public Action<StudioXMcpApprovalRequest>? OnApproval
    {
        get; set;
    }

    public Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Requests.Add(request);
        OnApproval?.Invoke(request);
        return Task.FromResult(AllowEnable && request.Tool == "ag32_pin_mapping_enable");
    }
}
