using System.Security.Cryptography;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

if (args is [var toolsets, "--recheck-evidence", var matrix, var recheckOutput])
{
    await EvidenceChecks.RunAsync(toolsets, matrix, recheckOutput);
    return;
}
if (args.Length != 3) throw new ArgumentException("toolsets pack-directory new-output");
var output = Path.GetFullPath(args[2]);
if (Directory.Exists(output)) throw new IOException("Use a new validation output directory.");
Directory.CreateDirectory(output);
var catalog = new ToolsetCatalog(Path.GetFullPath(args[0]));
var packs = new PackRepository(Path.Combine(output, "packs"));
var planner = new Ag32PinPlanService(catalog);
var timing = new Ag32TimingService(catalog);
var mapping = new Ag32PinMappingBuildService(catalog);
var checks = new List<string>();
var cases = new List<object>();
void Check(bool pass, string description)
{
    if (!pass) throw new InvalidOperationException(description);
    checks.Add(description);
    Console.WriteLine("PASS " + description);
    File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
}
var analog = new Ag32AnalogSettings(true, 1, true, true, true);
Check(Ag32ClockPolicy.Validate("AG32VF303CCT6", new(8, 160, 100)) is not null, "160/100 rejected before converter");
Check(Ag32ClockPolicy.Validate("AG32VF303CCT6", new(8, 160, 80)) is null, "160/80 structurally valid");
Check(Ag32ClockPolicy.Validate("AG32VF303CCT6", new(8, 100, 0)) is null, "BUS=0 retains SYS clock behavior");
Check(Ag32ClockPolicy.Validate("AG32VF303CCT6", new(8, null, 80)) is not null, "BUS needs explicit SYS");
Check(Ag32ClockPolicy.Validate("AG32VF303CCT6", new(null, 160, 80)) is not null, "SYS needs explicit HSE");
Check(Ag32ClockPolicy.Recommendations("AG32VF303CCT6", new(12, 160, 80), analog).Length == 0, "No guessed recommendation for another crystal");
Check(Ag32ClockPolicy.Recommendations("AG32VF303CCT6", new(8, 200, 100), new()).Length == 0, "Analog presets do not affect digital-only projects");

foreach (var archive in Directory.GetFiles(Path.GetFullPath(args[1]), "*.mcupack").Order())
{
    var pack = await packs.ImportAsync(archive);
    foreach (var device in pack.Manifest.Devices.Where(Ag32DeviceCatalog.Matches))
    {
        foreach (var sys in new[] { 100, 160, 200 })
        {
            var root = Path.Combine(output, device.Id, sys.ToString());
            await new ProjectService().CreateAsync(pack, device.Id, "minimal", "timing_regression", root);
            var snapshot = await planner.ReadAsync(root);
            await planner.ApplyAsync(root, snapshot, [new("UART0_UARTTXD", 2)], new(8, sys, sys / 2), analog);
            var before = await File.ReadAllBytesAsync(Path.Combine(root, "logic/pins.ve"));
            Check((await timing.ReadAsync(root)).State == Ag32TimingState.Unverified, device.Id + $" {sys}: unbuilt preset is not green");
            var main = await File.ReadAllBytesAsync(Path.Combine(root, "src/main.c"));
            var buildPassed = false;
            try
            {
                var build = await new BuildService(catalog).BuildAsync(root, new Progress<string>(Console.WriteLine));
                Check(build.Success, device.Id + $" {sys}: joint build returns success");
                buildPassed = true;
            }
            catch (StudioXException error) when (error.Code == "AG32_MAPPING_TIMING")
            {
                Check(sys == 200, device.Id + $" {sys}: timing failure only allowed in stress case");
                await File.WriteAllTextAsync(Path.Combine(root, "expected-timing-failure.txt"), error.ToString());
            }
            var status = await timing.ReadAsync(root);
            Check(status.Total > 0 && status.Total == status.Covered && status.SetupPath is not null && status.HoldPath is not null,
                device.Id + $" {sys}: actual coverage, setup and hold paths exposed");
            Check(buildPassed ? status.State is Ag32TimingState.Passed or Ag32TimingState.LowMargin
                : status.State == Ag32TimingState.Failed && (status.SetupSlackNs < 0 || status.HoldSlackNs < 0),
                device.Id + $" {sys}: status matches actual tool timing outcome");
            if (device.Id == "AG32VF303CCT6" && sys == 200)
                Check(!buildPassed && status.SetupSlackNs < 0, "Original 200/100 analog timing failure reproduced and shown in red state");
            if (buildPassed) _ = await mapping.RequireImageAsync(root);
            else
            {
                try { await mapping.RequireImageAsync(root); throw new InvalidOperationException("Failed timing image accepted"); }
                catch (StudioXException error) when (error.Code == "AG32_MAPPING_BUILD_REQUIRED")
                { Check(!File.Exists(Path.Combine(root, ".build/ag32-mapping/pins.bin")), "Failed image deleted and download blocked"); }
            }
            var mainAfter = await File.ReadAllBytesAsync(Path.Combine(root, "src/main.c"));
            Check(main.SequenceEqual(mainAfter), "Application main remains unchanged");
            cases.Add(new { device = device.Id, sysMhz = sys, busMhz = sys / 2, buildPassed, status,
                sourceSha256 = Convert.ToHexString(SHA256.HashData(before)) });
            await JsonStore.WriteAsync(Path.Combine(output, "matrix.json"), cases);

            if (device.Id == "AG32VF303CCT6")
            {
                var source = Path.Combine(root, "logic/pins.ve");
                await File.AppendAllTextAsync(source, "\n# input changed after routing\n");
                Check((await timing.ReadAsync(root)).State == Ag32TimingState.Stale, $"{sys}: VE edits invalidate both positive and failed timing evidence");
                await File.WriteAllBytesAsync(source, before);
                var reportPath = Path.Combine(root, ".build/ag32-mapping/logic_db/setup_summary.rpt.gz");
                var reportBytes = await File.ReadAllBytesAsync(reportPath);
                await File.WriteAllBytesAsync(reportPath, [0, 1, 2, 3]);
                Check((await timing.ReadAsync(root)).State == Ag32TimingState.Unverified, $"{sys}: corrupt report never appears green");
                await File.WriteAllBytesAsync(reportPath, reportBytes);
                var sdc = Path.Combine(root, ".build/ag32-mapping/studiox-clocks.sdc");
                var constraintBytes = await File.ReadAllBytesAsync(sdc);
                await File.AppendAllTextAsync(sdc, "\n# changed constraint\n");
                Check((await timing.ReadAsync(root)).State == Ag32TimingState.Stale, $"{sys}: changed constraint invalidates displayed timing");
                await File.WriteAllBytesAsync(sdc, constraintBytes);
                Check((await timing.ReadAsync(root)).State == status.State, $"{sys}: restored exact evidence yields original status");
            }
        }
    }
}
Check(cases.Count == 21, "Seven exact devices x three clock combinations routed");
await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new { success = true, checks = checks.Count, cases = cases.Count, hardwareConnected = false });
Console.WriteLine($"Completed {checks.Count} checks and {cases.Count} actual route cases; offline only.");
