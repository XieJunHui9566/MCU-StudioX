using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudioX.Application;
using StudioX.Application.Plugins;
using StudioX.Engine;
using StudioX.Extensions;
using StudioX.Foundation;
using StudioX.Packages;
using StudioX.StcConfigurator;
using StudioX.StcConfiguratorValidation;

if (args.Length is not (4 or 5) || args.Length == 5 && args[4] != "--peripherals") { throw new ArgumentException("Usage: <preview-pack> <plugin-archive> <runtime> <new-output> [--peripherals]"); }
var output = Path.GetFullPath(args[3]);
if (Directory.Exists(output)) { throw new ArgumentException("Use a new output directory."); }
Directory.CreateDirectory(output);
var checks = new List<string>();
void Check(bool passed, string name)
{
    if (!passed)
    {
        throw new InvalidOperationException(name);
    }
    checks.Add(name);
    Console.WriteLine("PASS " + name);
}
void Reject(Action operation, string name)
{
    try
    {
        operation();
    }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException or StudioXException or JsonException)
    {
        Check(true, name);
        return;
    }
    throw new InvalidOperationException("Expected rejection: " + name);
}
JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
string Canonical(string value) { using var doc = JsonDocument.Parse(value); return Encoding.UTF8.GetString(CanonicalJson.Encode(doc.RootElement)); }
Check(Canonical("{\"z\":1,\"a\":-0.0,\"s\":\"中文\"}") == "{\"a\":0,\"s\":\"中文\",\"z\":1}", "JCS ordinal property order, Unicode and negative zero");
foreach (var pair in new[] { ("1e-6", "0.000001"), ("1e-7", "1e-7"), ("1e20", "100000000000000000000"), ("1e21", "1e+21"), ("333333333.33333329", "333333333.3333333"), ("4.50", "4.5"), ("2e-3", "0.002") })
{
    Check(Canonical(pair.Item1) == pair.Item2, "JCS numeric representation " + pair.Item1);
}
Reject(() => Canonical("{\"a\":1,\"a\":2}"), "JCS rejects duplicate properties");
var repository = new PackRepository(Path.Combine(output, "packs"));
var pack = await repository.ImportAsync(Path.GetFullPath(args[0]));
var project = Path.Combine(output, "project");
var projectManifest = await new ProjectService().CreateAsync(pack, "IAP15L2K61S2", "bare", "stc_config_preview", project);
Check(projectManifest.GraphicalConfiguration?.Path == "device.mcuconfig", "template declares explicit project configuration association");
var path = Path.Combine(project, "device.mcuconfig");
var contents = await File.ReadAllTextAsync(path);
if (args.Length == 5)
{
    await PeripheralChecks.RunAsync(pack, Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), output, project, contents, Check, Reject);
    await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
    {
        passed = true,
        checks,
        project,
        hardwareConnected = false
    });
    Console.WriteLine("Validated " + checks.Count + " peripheral behaviors; no hardware session.");
    return;
}
PinAliasChecks.Run(contents, Path.Combine(project, "device/sdk/include/stc15.h"), Check);
var document = JsonNode.Parse(contents)!;
using (var definition = JsonDocument.Parse(document["definitions"]!.ToJsonString()))
{
    Check(CanonicalJson.Hash(definition.RootElement) == document["origin"]!["definitionsSha256"]!.GetValue<string>(), "project source binding has correct JCS definition hash");
}
Check(document["origin"]!["packContentHash"]!.GetValue<string>() == pack.ContentHash && document["documentKind"]!.GetValue<string>() == "project", "creation binds verified pack and project kind without executing plugin");
var model = new StcConfigurationModel(contents);
Check(model.View("", true).Pins.Length == 44 && model.View("", true).Pins.Single(pin => pin.Position == "14").Name == "VCC" && model.View("", true).Pins.Single(pin => pin.Position == "16").Name == "GND", "exact 44-pin package includes supply and ground positions");
model.Apply(Json(new { action = "assignPin", pinId = "pad-4", functionId = "gpio" }));
Check(model.Dirty && model.GenerateCode().Contains("P1M1", StringComparison.Ordinal), "GPIO assignment is in memory and emits register configuration");
var modified = (JsonObject)document.DeepClone();
modified["target"]!["deviceId"] = "IAP15F2K61S2";
Reject(() => _ = new StcConfigurationModel(modified.ToJsonString()), "reject F/L model substitution");
modified = (JsonObject)document.DeepClone();
modified["definitions"]!["pins"]![0]!["displayName"] = "P9.9";
Reject(() => _ = new StcConfigurationModel(modified.ToJsonString()), "reject altered pin definition snapshot");
model.Apply(Json(new { action = "assignPin", pinId = "pad-18", functionId = "gpio" }));
Check(model.Errors().Any(item => item.Contains("Mon51", StringComparison.Ordinal)), "Mon51 option reserves P3.0 and P3.1");
Reject(() => model.GenerateCode(), "reject generation with resource conflict");
model.Apply(Json(new { action = "assignPin", pinId = "pad-18", functionId = "" }));
model.Apply(Json(new { action = "selectPin", pinId = "pad-4" }));
Dictionary<string, string> Values(StcConfigurationModel current) => current.View("", true).Fields.ToDictionary(field => field.Id, field => field.Value, StringComparer.Ordinal);
void Parameters(StcConfigurationModel current, params (string Key, string Value)[] changes)
{
    var values = Values(current);
    foreach (var (key, value) in changes)
    {
        values[key] = value;
    }
    current.Apply(Json(new
    {
        action = "parameters",
        values
    }));
}
Parameters(model, ("uartRoute", "p36"));
Check(model.Errors().Any(item => item.Contains("时钟", StringComparison.Ordinal)) && model.Errors().Any(item => item.Contains("UART1", StringComparison.Ordinal)), "UART requires explicit clock and does not evade Mon51 by remapping");
Parameters(model, ("timer0", "1"));
Check(model.Errors().Any(item => item.Contains("监控保护", StringComparison.Ordinal)), "unverified monitor and timer coexistence remains conservatively blocked");
Parameters(model, ("reserve", "none"), ("clockSource", "IRC"), ("clockMHz", "24"), ("timer0", "1"), ("gpioMode", "pushPull"), ("gpioLevel", "0"), ("gpioLabel", "LED"));
Check(model.Errors().Count == 0, "explicit clock, GPIO, UART1 and Timer0 form a valid configuration");
model.Apply(Json(new { action = "pinLock", pinId = "pad-4", locked = true }));
var lockedSnapshot = model.Document.ToJsonString();
Reject(() => model.Apply(Json(new { action = "assignPin", pinId = "pad-4", functionId = "" })), "backend rejects clearing a locked pin");
Check(model.Document.ToJsonString() == lockedSnapshot, "rejected lock changes leave the entire document unchanged");
var restoredLocks = new StcConfigurationModel(model.Document.ToJsonString());
Check(restoredLocks.View("", true).Pins.Single(pin => pin.Id == "pad-4") is { Locked: true, Label: "LED" }, "pin name and lock survive document reload");
model.Apply(Json(new { action = "pinLock", pinId = "pad-4", locked = false }));
var uartPin = model.View("", true).Pins.Single(pin => pin.Name == "P3.6").Id;
model.Apply(Json(new { action = "pinLabel", pinId = uartPin, label = "CONSOLE_RX" }));
model.Apply(Json(new { action = "pinLock", pinId = uartPin, locked = true }));
Parameters(model, ("systemDivider", "2"));
Check(model.View("", true).Pins.Single(pin => pin.Id == uartPin) is { Locked: true, Label: "CONSOLE_RX" }, "unrelated parameter updates preserve UART labels and locks");
Reject(() => Parameters(model, ("uartRoute", "default")), "peripheral routing cannot bypass a pin lock");
model.Apply(Json(new { action = "pinLock", pinId = uartPin, locked = false }));
model.Apply(Json(new { action = "pinLabel", pinId = uartPin, label = "" }));
Parameters(model, ("systemDivider", "1"));
var generated = model.GenerateCode();
Check(generated.Contains("P_SW1 = (P_SW1 & 0x3F) | 0x40", StringComparison.Ordinal) && generated.Contains("T2L", StringComparison.Ordinal) && generated.Contains("TR0 = 0", StringComparison.Ordinal), "UART1 remap and baud generator plus stopped Timer0 output");
Check(generated.Contains("P1 = (P1 & 0xFE) | 0x00", StringComparison.Ordinal) && generated.Contains("P1M0 = (P1M0 & 0xFE) | 0x01", StringComparison.Ordinal), "GPIO initializes latch before push-pull while preserving other bits");
Parameters(model, ("clockSource", "XTAL"), ("uartRoute", "p16"));
Check(model.Errors().Any(item => item.Contains("晶振", StringComparison.Ordinal)), "external crystal conflicts with P1.6/P1.7 remap");
Parameters(model, ("clockSource", "IRC"), ("uartRoute", "p36"), ("period0", "1000000"));
Check(model.Errors().Any(item => item.Contains("16 位", StringComparison.Ordinal)), "timer rejects reload overflow");
Parameters(model, ("period0", "1000"));
Reject(() => Parameters(model, ("clockMHz", "12.1234567")), "MHz input rejects sub-Hz precision");
Reject(() => Parameters(model, ("clockMHz", "28.1")), "MHz input rejects frequency above device range");
Check(model.Errors().Count == 0, "failed parameter update preserves previous configuration");
Parameters(model, ("clockMHz", "12.345678"), ("uartRoute", "off"));
Check(model.Document["configuration"]!["clocks"]!.AsArray().Single(node => node!["nodeId"]!.GetValue<string>() == "IRC")!["parameters"]!["frequencyHz"]!.GetValue<int>() == 12345678,
    "editable MHz accepts custom frequency and preserves exact integer Hz");
var frequencyField = model.View("", true).Fields.Single(field => field.Id == "clockMHz");
Check(frequencyField.Kind == "editableSelect" && frequencyField.Options.Select(option => option.Value).SequenceEqual(new[] { "5.5296", "6", "11.0592", "12", "18.432", "20", "22.1184", "24", "27" }), "editable combo presets match exact IAP15L2K61S2 STC-ISP list");
Parameters(model, ("clockMHz", "24"), ("uartRoute", "p36"));
Check(model.View("", true).Clocks.Single(node => node.Kind == "multiplexer").ActiveInputIds is ["IRC"], "clock multiplexer selects only internal RC path");
var state = JsonSerializer.SerializeToElement(model.View("validation", true), new JsonSerializerOptions(JsonSerializerDefaults.Web));
Check(PluginMcuConfiguration.Parse(state).Pins.Length == 44, "host validates actual plugin graphical data");
var badView = JsonNode.Parse(state.GetRawText())!;
badView["pins"]![0]!["x"] = -1;
Reject(() => PluginMcuConfiguration.Parse(JsonSerializer.SerializeToElement(badView)), "host rejects out-of-package pin geometry");
var badClock = JsonNode.Parse(state.GetRawText())!;
badClock["clocks"]![0]!["kind"] = "xaml";
Reject(() => PluginMcuConfiguration.Parse(JsonSerializer.SerializeToElement(badClock)), "clock view refuses undeclared drawing symbols");
badClock = JsonNode.Parse(state.GetRawText())!;
badClock["clocks"]![0]!["x"] = 2401;
Reject(() => PluginMcuConfiguration.Parse(JsonSerializer.SerializeToElement(badClock)), "clock view bounds drawing coordinates");
badClock = JsonNode.Parse(state.GetRawText())!;
badClock["clocks"]![2]!["activeInputIds"] = new JsonArray("missing-input");
Reject(() => PluginMcuConfiguration.Parse(JsonSerializer.SerializeToElement(badClock)), "clock view rejects an active path outside declared inputs");
await File.WriteAllTextAsync(path, model.Document.ToJsonString() + "\n", new UTF8Encoding(false));
await File.WriteAllTextAsync(Path.Combine(output, "generated-preview.c"), model.GenerateCode(), new UTF8Encoding(false));

// 编译只在隔离工程内进行；不通过配置插件触发任何设备操作。
var runtime = Path.GetFullPath(args[2]);
var builder = new BuildService(new ToolsetCatalog(Path.Combine(runtime, "toolsets")));
var initial = await builder.BuildAsync(project);
Check(initial.Success, "plain template builds without loading configuration plugin");
var initialSource = await File.ReadAllTextAsync(Path.Combine(project, "src/main.c"));
var matrix = new List<string>();
foreach (var uartRoute in new[] { "off", "default", "p36", "p16" })
{
    Parameters(model, ("uartRoute", uartRoute), ("clockSource", "IRC"), ("clockMHz", "24"), ("reserve", "none"));
    foreach (var divisor in new[] { "1", "12" })
    {
        Parameters(model, ("timer0", divisor), ("timer1", divisor), ("period0", "1000"), ("period1", "1000"));
        var source = model.GenerateCode() + "\nvoid main(void)\n{\n    (void)sx_mcuconfig_init();\n    for (;;) { }\n}\n";
        await File.WriteAllTextAsync(Path.Combine(project, "src/main.c"), source, new UTF8Encoding(false));
        var result = await builder.BuildAsync(project);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Log);
        }
        matrix.Add(uartRoute + "/" + divisor + "T");
        File.Copy(result.LogPath ?? throw new InvalidOperationException("构建没有原始日志。"), Path.Combine(output, "compile-" + uartRoute + "-" + divisor + ".log"));
    }
}
await File.WriteAllTextAsync(Path.Combine(project, "src/main.c"), initialSource, new UTF8Encoding(false));
Check(matrix.Count == 8, "eight actual SDCC builds cover UART routes and 1T/12T timers");

var pluginRepository = new PluginRepository(Path.Combine(output, "plugin-repository"));
var installed = await pluginRepository.ImportAsync(Path.GetFullPath(args[1]));
var pluginManifestPath = installed.ManifestPath;
var hostExe = Path.Combine(AppContext.BaseDirectory, "runtime/plugin-host/StudioX.PluginHost.exe");
if (!File.Exists(hostExe)) { hostExe = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(AppContext.BaseDirectory)))!, "StudioX.PluginHost/release_win-x64/StudioX.PluginHost.exe"); }
var activeDocument = await File.ReadAllTextAsync(path);
var operations = new List<string>();
await using (var client = await PluginRuntimeClient.StartAsync(hostExe, pluginManifestPath, async (tool, arguments, token) =>
{
    operations.Add(tool);
    if (tool == "project_info")
    {
        return Json(new
        {
            projectManifest.DeviceId,
            projectManifest.PackId,
            projectManifest.PackVersion,
            projectManifest.TemplateId,
            projectManifest.CompilerId,
            projectManifest.PackContentHash,
            graphicalConfiguration = projectManifest.GraphicalConfiguration
        });
    }
    if (tool == "editor_read")
    {
        var offset = arguments.GetProperty("offset").GetInt32();
        var count = Math.Min(16000, activeDocument.Length - offset);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(activeDocument)));
        return Json(new
        {
            text = activeDocument.Substring(offset, count),
            contentHash = hash,
            diskHash = hash,
            isDirty = false,
            nextOffset = offset + count < activeDocument.Length ? (int?)(offset + count) : null
        });
    }
    await Task.CompletedTask;
    throw new InvalidOperationException("Unexpected host operation: " + tool);
}, (item, token) => Task.CompletedTask))
{
    var result = await client.InvokeAsync("command", "interact", Json(new
    {
        action = "validate"
    }));
    Check(PluginMcuConfiguration.Parse(result).CanSave, "real isolated host activates exact project and returns validated view");
    Check(operations.All(tool => tool is "project_info" or "editor_read"), "activation and validation have no file-write or device calls");
}
var isolatedRuntime = Path.Combine(output, "isolated-runtime");
var isolatedHost = Path.Combine(isolatedRuntime, "plugin-host");
Directory.CreateDirectory(isolatedHost);
foreach (var file in Directory.GetFiles(Path.GetDirectoryName(hostExe)!)) { File.Copy(file, Path.Combine(isolatedHost, Path.GetFileName(file))); }
await using (var services = new WorkbenchService(isolatedRuntime, Path.Combine(output, "host-data")))
{
    var authorizer = new ConfigurationWriteAuthorizer();
    var unsaved = false;
    await using var broker = new PluginWorkspaceBroker(services, project, authorizer, () => Task.FromResult(unsaved));
    var imported = await services.PluginManager.ImportAsync(Path.GetFullPath(args[1]));
    Check(!imported.Enabled, "plugin import does not execute or trust code");
    await services.PluginManager.SetEnabledAsync(imported.Id, true);
    await using var workspace = await services.PluginManager.OpenWorkspaceAsync(project, broker.CallAsync);
    var configured = await workspace.InvokeAsync(imported.Id, "command", "interact", Json(new
    {
        action = "assignPin",
        pinId = "pad-40",
        functionId = "gpio"
    }));
    Check(PluginMcuConfiguration.Parse(configured).Dirty, "real application broker preserves plugin configuration draft");
    var before = await File.ReadAllTextAsync(path);
    var savePlan = await workspace.InvokeAsync(imported.Id, "command", "interact", Json(new
    {
        action = "save"
    }));
    Check(savePlan.GetProperty("formatVersion").GetInt32() == 1 && await File.ReadAllTextAsync(path) == before && authorizer.Requests == 0,
        "direct save invocation returns a pure candidate without writes or approval requests");
    Task Save() => services.McuCodeGeneration.SaveConfigurationAsync(workspace, imported.Id, "interact", () => Task.FromResult(unsaved));
    unsaved = true;
    try
    {
        await Save();
    }
    catch (StudioXException) { }
    Check(await File.ReadAllTextAsync(path) == before && authorizer.Requests == 0, "unsaved editor buffers block explicit configuration save");
    unsaved = false;
    await Save();
    await services.McuCodeGeneration.RefreshSavedConfigurationAsync(workspace, imported.Id, "interact");
    Check(await File.ReadAllTextAsync(path) != before && authorizer.Requests == 0, "explicit configuration save commits without redundant approval");
    Check(await File.ReadAllTextAsync(Path.Combine(project, "src/main.c")) == initialSource, "configuration save does not generate or overwrite source code");
    var external = await File.ReadAllTextAsync(path) + " \n";
    await File.WriteAllTextAsync(path, external, new UTF8Encoding(false));
    await workspace.InvokeAsync(imported.Id, "command", "interact", Json(new
    {
        action = "assignPin",
        pinId = "pad-41",
        functionId = "gpio"
    }));
    try
    {
        await Save();
    }
    catch (StudioXException) { }
    Check(await File.ReadAllTextAsync(path) == external, "external disk change is never overwritten by stale plugin state");
}
await ManagedClockChecks.RunAsync(pack, Path.GetFullPath(args[1]), runtime, isolatedRuntime, output, Check);
await GenerationChecks.RunAsync(pack, Path.GetFullPath(args[1]), runtime, isolatedRuntime, output, Check);
await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new { passed = true, checks, compileMatrix = matrix, project, hardwareConnected = false });
Console.WriteLine("Validated " + checks.Count + " behaviors; no hardware session.");
