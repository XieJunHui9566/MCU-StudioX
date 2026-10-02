namespace StudioX.ProductWorkflowValidation;

using System.Text.RegularExpressions;
using StudioX.Application;
using StudioX.Application.Components;
using StudioX.Application.Tools;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;
using StudioX.Packages;

internal static class NativeChecks
{
    public static async Task RunAsync(string root, string toolsets, string packArchive, Action<bool, string> check)
    {
        var packs = new PackRepository(Path.Combine(root, "native-packs"));
        var pack = await packs.ImportAsync(packArchive);
        var device = pack.Manifest.Devices.Single(d => d.Id == "STM32F407ZG");
        var template = device.Templates.First(t => t.Id.Contains("hal", StringComparison.OrdinalIgnoreCase) && !t.Id.Contains("rtos", StringComparison.OrdinalIgnoreCase));
        var project = Path.Combine(root, "native-project");
        await new ProjectService().CreateAsync(pack, device.Id, template.Id, "workflow-native", project);
        var catalog = new ToolsetCatalog(toolsets);
        var preparation = new ProjectToolPreparationService(catalog, new ToolManagementService(catalog, packs, new RecentProjectService(root), root));
        check((await preparation.InspectAsync(project)).Requirements.All(r => r.State == ProjectToolState.Installed), "generated F407 project recognizes installed tools without full SDK scan");
        var build = new BuildService(catalog);
        var history = new BuildHistoryService(Path.Combine(root, "native-data"), new(catalog));
        var baseline = await build.BuildAsync(project);
        await File.WriteAllTextAsync(Path.Combine(root, "native-baseline.log"), baseline.Log);
        check(baseline.Success, "isolated F407 HAL baseline compiles with installed locked tools");
        var before = await history.CaptureAsync(project);
        check(before.Memory.Targets.Count > 0 && before.Details.Contributions.Count > 0 && before.Details.Timings.Count > 0, "native snapshot includes actual MAP contributions and Ninja timings");
        var component = new ComponentService(() => false, build);
        await component.InstallAsync(project, await component.PreviewAsync(FixtureArchives.Component(root, "1.0.0", "fixture.native")), "firmware");
        var main = Path.Combine(project, "src/main.c");
        var text = await File.ReadAllTextAsync(main);
        text = "#include \"component.h\"\nstatic volatile unsigned component_result;\n" + text;
        text = Regex.Replace(text, @"(int\s+main\s*\(\s*void\s*\)\s*\{)", "$1\n component_result = studiox_component_value();", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        await File.WriteAllTextAsync(main, text);
        var rebuilt = await build.BuildAsync(project);
        await File.WriteAllTextAsync(Path.Combine(root, "native-component.log"), rebuilt.Log);
        check(rebuilt.Success, "component registration actually compiles and links into F407 HAL project");
        var after = await history.CaptureAsync(project);
        await JsonStore.WriteAsync(Path.Combine(root, "native-history.json"), new[] { before, after });
        check(after.Details.Contributions.Any(c => c.Name.Contains("studiox_component_value")) && BuildHistoryService.Compare(before, after).Any(r => r.Difference != 0), "two genuine builds expose new function and nonzero memory differences");
        var tools = await catalog.ResolveAsync(device.ToolsetId, device.ToolsetVersion, device.CompilerId);
        var elf = rebuilt.Artifacts.FirstOrDefault(p => p.EndsWith(".elf", StringComparison.OrdinalIgnoreCase)) ?? Path.Combine(project, ".build/firmware.elf");
        var symbols = await new ProcessRunner().RunAsync(new(tools.Tool("objdump"), ["-t", elf], project, TimeSpan.FromSeconds(30), ToolsetEnvironment.Create(tools), RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
        var match = Regex.Match(symbols.StandardOutput, @"(?m)^([0-9a-fA-F]{8})[^\r\n]*\bmain\s*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        check(match.Success, "actual ELF provides main address for offline fault localization");
        var address = Convert.ToUInt32(match.Groups[1].Value, 16);
        var report = FaultAnalyzer.Analyze(new(1, device.Id, "offline constructed fault record", DateTimeOffset.UtcNow, 1u << 25, 1u << 30, null, null, address, null, "fixture; not board data"));
        var located = await new FirmwareFaultService(catalog).SymbolizeAsync(project, report);
        check(located.ElfSha256 is not null && located.SymbolInformation.Contains("main") && !located.Evidence.FirmwareMatched, "managed GDB maps imported address to source without device connection");
        await File.AppendAllTextAsync(main, "\n/* changed after build */\n");
        try { await history.CaptureAsync(project); check(false, "stale build snapshot"); }
        catch (StudioXException error) { check(error.Code == "BUILD_HISTORY_STALE", "source change prevents recording stale build as current"); }
        try { await new FirmwareFaultService(catalog).SymbolizeAsync(project, report); check(false, "stale fault symbols"); }
        catch (StudioXException error) { check(error.Code == "FAULT_ELF", "source change prevents mapping old fault ELF onto changed source"); }
    }
}
