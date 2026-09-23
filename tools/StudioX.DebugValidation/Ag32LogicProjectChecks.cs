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
        if (Directory.Exists(root)) throw new InvalidOperationException("Use a new validation directory.");
        Directory.CreateDirectory(root);
        var pack = await new PackRepository(Path.Combine(root, "repository")).ImportAsync(archive);
        var service = new ProjectService();
        var template = pack.Manifest.Devices.Single(d => d.Id == "AG32VF303CCT6").Templates.First().Id;
        var ordinaryPath = Path.Combine(root, "普通 AG32 工程");
        var logicPath = Path.Combine(root, "AG32 逻辑 工程");
        var results = new List<string>();
        void Pass(string message) { results.Add(message); Console.WriteLine("PASS " + message); }

        var ordinary = await service.CreateAsync(pack, "AG32VF303CCT6", template, "OrdinaryAG32", ordinaryPath);
        Check(ordinary.Logic is null && !Directory.Exists(Path.Combine(ordinaryPath, "logic")),
            "Default AG32 project must not create logic files");
        Check((await ProjectService.ReadAsync(ordinaryPath)).Logic is null, "Default mode persists as disabled");
        var ordinaryManifestPath = Path.Combine(ordinaryPath, ".studiox", "project.json");
        var ordinaryManifest = await File.ReadAllTextAsync(ordinaryManifestPath);
        try
        {
            var legacyDocument = JsonNode.Parse(ordinaryManifest)!.AsObject();
            legacyDocument.Remove("logic");
            await File.WriteAllTextAsync(ordinaryManifestPath, legacyDocument.ToJsonString(JsonStore.Options));
            Check((await ProjectService.ReadAsync(ordinaryPath)).Logic is null,
                "Existing projects without the new option must reopen in MCU-only mode");
        }
        finally { await File.WriteAllTextAsync(ordinaryManifestPath, ordinaryManifest); }
        var workflow = new Ag32LogicWorkflowService();
        await RejectAsync(() => workflow.InspectAsync(ordinaryPath), "AG32_LOGIC_DISABLED");
        Pass("AG32 default and existing project modes stay MCU-only after reopen");

        var enabled = await service.CreateAsync(pack, "AG32VF303CCT6", template, "LogicAG32", logicPath,
            enableAg32Logic: true);
        Check(enabled.Logic is { TargetDevice: "AGRV2KL48", VerilogFile: "logic/user_logic.v", PinMapFile: "logic/pins.ve" },
            "Selected AG32 logic mode must specify the LQFP48 device and files");
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

        var alienDevice = pack.Manifest.Devices.Single() with { Id = "OTHER48" };
        var alienPack = pack with { Manifest = pack.Manifest with { Devices = [alienDevice] } };
        Reject(() => ProjectService.Plan(alienPack, alienDevice.Id, template, "Other", enableAg32Logic: true),
            "PROJECT_LOGIC_DEVICE");
        var alienVendor = pack with { Manifest = pack.Manifest with { Vendor = "Other" } };
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

        await File.WriteAllLinesAsync(Path.Combine(root, "result.txt"), results.Prepend("PASS — offline only; no logic tools or hardware used"));
        return 0;
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Reject(Action action, string code)
    {
        try { action(); } catch (StudioXException ex) when (ex.Code == code) { return; }
        throw new InvalidOperationException("Expected rejection " + code);
    }

    private static async Task RejectAsync(Func<Task> action, string code)
    {
        try { await action(); } catch (StudioXException ex) when (ex.Code == code) { return; }
        throw new InvalidOperationException("Expected rejection " + code);
    }
}
