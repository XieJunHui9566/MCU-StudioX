using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

// 仅在隔离目录创建工程；不启动 Quartus、Supra、OpenOCD，也不访问硬件。
internal static class Ag32LogicProjectChecks
{
    public static async Task<int> RunAsync(string archive, string output)
    {
        var root = Path.GetFullPath(output);
        if (Directory.Exists(root))
        {
            throw new InvalidOperationException("Use a new validation directory.");
        }
        Directory.CreateDirectory(root);
        var pack = await new PackRepository(Path.Combine(root, "repository")).ImportAsync(archive);
        var service = new ProjectService();
        var template = pack.Manifest.Devices.Single(d => d.Id == "AG32VF303CCT6").Templates.First().Id;
        var ordinaryPath = Path.Combine(root, "普通 AG32 工程");
        var logicPath = Path.Combine(root, "AG32 逻辑 工程");
        var results = new List<string>();
        void Pass(string message)
        {
            results.Add(message);
            Console.WriteLine("PASS " + message);
        }

        var ordinary = await service.CreateAsync(pack, "AG32VF303CCT6", template, "OrdinaryAG32", ordinaryPath);
        Check(ordinary.Logic is null && ordinary.PinMapping == new Ag32PinMappingProjectSettings("AGRV2KL48") &&
              File.Exists(Path.Combine(ordinaryPath, "logic", "pins.ve")) && !File.Exists(Path.Combine(ordinaryPath, "logic", "user_logic.v")),
            "Default AG32 project must create basic pin mapping without custom Verilog");
        Check((await ProjectService.ReadAsync(ordinaryPath)).PinMapping == ordinary.PinMapping, "Basic mapping mode persists");
        var ordinaryPinMapPath = Path.Combine(ordinaryPath, "logic", "pins.ve");
        var ordinaryPinMap = await File.ReadAllTextAsync(ordinaryPinMapPath);
        Check(!ordinaryPinMap.Split('\n').Select(line => line.Split('#', 2)[0]).Any(line =>
                Regex.IsMatch(line, @"\b(?:PIN_\d+|(?:HSE|SYS|BUS)CLK)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
            "Basic scaffold must not guess board pins or clock frequencies");
        var ordinaryManifestPath = Path.Combine(ordinaryPath, ".studiox", "project.json");
        var ordinaryManifest = await File.ReadAllTextAsync(ordinaryManifestPath);
        try
        {
            var legacyDocument = JsonNode.Parse(ordinaryManifest)!.AsObject();
            legacyDocument.Remove("logic");
            legacyDocument.Remove("pinMapping");
            await File.WriteAllTextAsync(ordinaryManifestPath, legacyDocument.ToJsonString(JsonStore.Options));
            Check((await ProjectService.ReadAsync(ordinaryPath)) is { Logic: null, PinMapping: null },
                "Existing projects without new options must reopen without implicit mapping enablement");
            var preservedBytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes("# 原工程映射\r\nGPIO4_4 PIN_21\r\n")).ToArray();
            await File.WriteAllBytesAsync(ordinaryPinMapPath, preservedBytes);
            var upgraded = await ProjectService.EnableAg32PinMappingAsync(ordinaryPath);
            Check(upgraded.PinMapping == ordinary.PinMapping && upgraded.Logic is null &&
                  (await File.ReadAllBytesAsync(ordinaryPinMapPath)).SequenceEqual(preservedBytes),
                "Explicitly enabling an old LED project must preserve exact VE bytes including BOM and line endings");
            var enabledManifestBytes = await File.ReadAllBytesAsync(ordinaryManifestPath);
            Check(await ProjectService.EnableAg32PinMappingAsync(ordinaryPath) == upgraded &&
                  (await File.ReadAllBytesAsync(ordinaryManifestPath)).SequenceEqual(enabledManifestBytes),
                "Already enabled project must be idempotent without rewriting metadata");
            File.Delete(ordinaryPinMapPath);
            Check((await ProjectService.EnableAg32PinMappingAsync(ordinaryPath)).PinMapping == ordinary.PinMapping && File.Exists(ordinaryPinMapPath),
                "Explicit enable can recover a missing basic VE without rebuilding the project");
        }
        finally
        {
            await File.WriteAllTextAsync(ordinaryManifestPath, ordinaryManifest);
            await File.WriteAllTextAsync(ordinaryPinMapPath, ordinaryPinMap);
        }
        var workflow = new Ag32LogicWorkflowService();
        await RejectAsync(() => workflow.InspectAsync(ordinaryPath), "AG32_LOGIC_DISABLED");
        Pass("Default AG32 has basic VE; legacy reopen stays unchanged; explicit enable preserves existing VE bytes and is idempotent");

        var enabled = await service.CreateAsync(pack, "AG32VF303CCT6", template, "LogicAG32", logicPath,
            enableAg32Logic: true);
        Check(enabled.Logic is { TargetDevice: "AGRV2KL48", VerilogFile: "logic/user_logic.v", PinMapFile: "logic/pins.ve" },
            "Selected AG32 logic mode must specify the LQFP48 device and files");
        Check(enabled.PinMapping == ordinary.PinMapping, "Custom Verilog project retains its VE metadata as a distinct configuration");
        await RejectAsync(() => ProjectService.EnableAg32PinMappingAsync(logicPath), "PROJECT_PIN_MAPPING_CUSTOM_LOGIC");
        Check(File.Exists(Path.Combine(logicPath, "logic", "user_logic.v")) &&
              File.Exists(Path.Combine(logicPath, "logic", "pins.ve")) &&
              File.Exists(Path.Combine(logicPath, "logic", "README.md")),
            "Selected mode must create editable logic scaffold");
        var pinMap = await File.ReadAllTextAsync(Path.Combine(logicPath, "logic", "pins.ve"));
        Check(!pinMap.Split('\n').Select(line => line.Split('#', 2)[0]).Any(line =>
                  Regex.IsMatch(line, @"\bPIN_\d+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
            "Scaffold must not assign unverified physical pins");
        Check((await ProjectService.ReadAsync(logicPath)).Logic == enabled.Logic, "Selected mode persists after reopen");
        var inspection = await workflow.InspectAsync(logicPath);
        Check(inspection.Settings.TargetDevice == "AGRV2KL48" &&
              inspection.BinaryPath == Path.Combine(logicPath, "logic", "pins.bin") &&
              inspection.Errors.Count == 0 && inspection.BinaryBytes is null &&
              inspection.Notes.Any(note => note.Contains("尚未找到逻辑 BIN", StringComparison.Ordinal)),
            "New logic scaffold must inspect cleanly without pretending a binary was built");
        Pass("AGRV2KL48 logic scaffold and option persist across reopen, including a Unicode path");

        var pinMapPath = Path.Combine(logicPath, "logic", "pins.ve");
        try
        {
            await File.WriteAllTextAsync(pinMapPath, "LED_A PIN_49:OUTPUT\n");
            Check((await workflow.InspectAsync(logicPath)).Errors.Any(e => e.Contains("PIN_49", StringComparison.Ordinal)),
                "Pin outside LQFP48 must be rejected");
            await File.WriteAllTextAsync(pinMapPath, "LED_A PIN_1:OUTPUT\nLED_B PIN_1:OUTPUT\n");
            var reusedPin = await workflow.InspectAsync(logicPath);
            Check(reusedPin.Notes.Any(note => note.Contains("PIN_1 也出现在", StringComparison.Ordinal)) &&
                  reusedPin.Errors.Count == 0,
                "Potentially valid pin sharing must be reported for review without a hard rejection");
        }
        finally { await File.WriteAllTextAsync(pinMapPath, pinMap); }
        Pass("Logic inspection rejects out-of-range pins and flags possible multiplexing for review");

        var alienDevice = pack.Manifest.Devices.Single() with
        {
            Id = "OTHER48"
        };
        var alienPack = pack with
        {
            Manifest = pack.Manifest with
            {
                Devices = [alienDevice]
            }
        };
        Reject(() => ProjectService.Plan(alienPack, alienDevice.Id, template, "Other", enableAg32Logic: true),
            "PROJECT_LOGIC_DEVICE");
        var alienVendor = pack with
        {
            Manifest = pack.Manifest with
            {
                Vendor = "Other"
            }
        };
        Reject(() => ProjectService.Plan(alienVendor, "AG32VF303CCT6", template, "Other", enableAg32Logic: true),
            "PROJECT_LOGIC_DEVICE");
        Pass("Logic mode rejects other device and vendor combinations");

        var manifestPath = Path.Combine(logicPath, ".studiox", "project.json");
        var original = await File.ReadAllTextAsync(manifestPath);
        try
        {
            var document = JsonNode.Parse(original)!;
            document["logic"]!["targetDevice"] = "AGRV2KL100";
            await File.WriteAllTextAsync(manifestPath, document.ToJsonString(JsonStore.Options));
            await RejectAsync(() => ProjectService.ReadAsync(logicPath), "PROJECT_LOGIC_SETTINGS");
        }
        finally { await File.WriteAllTextAsync(manifestPath, original); }
        Pass("Reopen rejects a tampered 100-pin logic target");

        try
        {
            var document = JsonNode.Parse(ordinaryManifest)!;
            document["pinMapping"]!["targetDevice"] = "AGRV2KL100";
            await File.WriteAllTextAsync(ordinaryManifestPath, document.ToJsonString(JsonStore.Options));
            await RejectAsync(() => ProjectService.ReadAsync(ordinaryPath), "PROJECT_PIN_MAPPING_SETTINGS");
            document["pinMapping"]!["targetDevice"] = "AGRV2KL48";
            document["pinMapping"]!["pinMapFile"] = "../pins.ve";
            await File.WriteAllTextAsync(ordinaryManifestPath, document.ToJsonString(JsonStore.Options));
            await RejectAsync(() => ProjectService.ReadAsync(ordinaryPath), "PROJECT_PIN_MAPPING_SETTINGS");
            document["pinMapping"]!["pinMapFile"] = "logic/pins.ve";
            document["pinMapping"]!["toolsetVersion"] = "9.9.9";
            await File.WriteAllTextAsync(ordinaryManifestPath, document.ToJsonString(JsonStore.Options));
            await RejectAsync(() => ProjectService.ReadAsync(ordinaryPath), "PROJECT_PIN_MAPPING_SETTINGS");
            document["pinMapping"] = null;
            document["deviceId"] = "OTHER48";
            await File.WriteAllTextAsync(ordinaryManifestPath, document.ToJsonString(JsonStore.Options));
            await RejectAsync(() => ProjectService.EnableAg32PinMappingAsync(ordinaryPath), "PROJECT_PIN_MAPPING_DEVICE");
        }
        finally { await File.WriteAllTextAsync(ordinaryManifestPath, ordinaryManifest); }
        var localPackPath = Path.Combine(ordinaryPath, "device", "manifest.json");
        var localPack = await File.ReadAllTextAsync(localPackPath);
        try
        {
            var document = JsonNode.Parse(localPack)!;
            document["vendor"] = "Other";
            await File.WriteAllTextAsync(localPackPath, document.ToJsonString(JsonStore.Options));
            await RejectAsync(() => ProjectService.EnableAg32PinMappingAsync(ordinaryPath), "PROJECT_PIN_MAPPING_DEVICE");
        }
        finally { await File.WriteAllTextAsync(localPackPath, localPack); }
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try
            {
                await ProjectService.EnableAg32PinMappingAsync(ordinaryPath, cancellation.Token);
                throw new InvalidOperationException("Expected cancelled enable");
            }
            catch (OperationCanceledException) { }
            Check(await File.ReadAllTextAsync(ordinaryManifestPath) == ordinaryManifest && await File.ReadAllTextAsync(ordinaryPinMapPath) == ordinaryPinMap,
                "Cancelled enable must preserve metadata and VE");
        }
        Pass("Basic mapping rejects unsupported target/path/tool version, alien device/vendor, custom Verilog substitution and preserves cancellation state");

        await File.WriteAllLinesAsync(Path.Combine(root, "result.txt"), results.Prepend("PASS — offline only; no logic tools or hardware used"));
        return 0;
    }

    private static void Check(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Reject(Action action, string code)
    {
        try
        {
            action();
        }
        catch (StudioXException ex) when (ex.Code == code) { return; }
        throw new InvalidOperationException("Expected rejection " + code);
    }

    private static async Task RejectAsync(Func<Task> action, string code)
    {
        try
        {
            await action();
        }
        catch (StudioXException ex) when (ex.Code == code) { return; }
        throw new InvalidOperationException("Expected rejection " + code);
    }
}
