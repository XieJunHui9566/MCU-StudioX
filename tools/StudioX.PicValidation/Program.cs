using System.Text.Json;
using StudioX.Application;
using StudioX.Application.CodeIntelligence;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;
using StudioX.PicValidation;

if (args is ["--catalog-matrix", var matrixEvidence, var matrixRuntime, var matrixArchives])
{
    await CatalogMatrix.RunAsync(Path.GetFullPath(matrixEvidence), Path.GetFullPath(matrixRuntime), Path.GetFullPath(matrixArchives));
    return;
}
if (args is not [var evidence, var runtime, var archive])
{
    throw new ArgumentException("<new-evidence> <runtime> <pack>");
}
evidence = Path.GetFullPath(evidence);
runtime = Path.GetFullPath(runtime);
if (Directory.Exists(evidence))
{
    throw new ArgumentException("Use new evidence directory");
}
Directory.CreateDirectory(evidence);
var checks = new List<string>();
void Check(bool value, string label)
{
    if (!value)
    {
        throw new InvalidOperationException(label);
    }
    checks.Add(label);
    Console.WriteLine("PASS " + label);
}
async Task Reject(string code, Func<Task> action)
{
    try
    {
        await action();
        throw new InvalidOperationException("Expected " + code);
    }
    catch (StudioXException error) when (error.Code == code)
    {
        Check(true, code + " rejected");
    }
}
await using var services = new WorkbenchService(runtime, Path.Combine(evidence, "data"));
var pack = await services.Packs.ImportAsync(Path.GetFullPath(archive));
var device = pack.Manifest.Devices.Single();
Check(device.Pic is { ProgramWords: 8192, ProgramWordBits: 14, EepromBytes: 256 } && device.RamBytes == 368, "exact PIC16F877A memory units retained");
foreach (var invalid in new[] { device with { FlashBytes = 8192 }, device with { Pic = device.Pic! with { CompilerTarget = "16F876A" } } })
{
    try
    {
        PackValidator.Validate(pack.Manifest with
        {
            Devices = new[] { invalid }
        }, pack.RootDirectory);
        throw new InvalidOperationException("Invalid PIC accepted");
    }
    catch (StudioXException error) when (error.Code == "PACK_PIC")
    {
        Check(true, "incorrect PIC identity or byte/word units rejected");
    }
}
var projects = new Dictionary<string, string>();
foreach (var template in device.Templates)
{
    var root = Path.Combine(evidence, "中文 projects with spaces", template.Id);
    var project = await services.Projects.CreateAsync(pack, device.Id, template.Id, "PicExample", root);
    Check(project.Kind == ProjectKind.Pic && project.Pic == device.Pic && project.PackContentHash == pack.ContentHash, template.Id + " records chip, component and pack identity");
    Check(!File.Exists(Path.Combine(root, "CMakeLists.txt")) && Directory.Exists(Path.Combine(root, ".git")), template.Id + " creates XC8 C project and bundled Git without CMake");
    var health = await services.ProjectHealth.InspectAsync(root);
    Check(health.CanBuild && health.Errors == 0 && !health.Checks.Any(item => item.Code == "HEALTH_CMAKE"), "PIC health checks the correct independent compiler");
    var build = await services.Builds.BuildAsync(root);
    await File.WriteAllTextAsync(Path.Combine(evidence, template.Id + ".log"), build.Log);
    Check(build.Success && build.ExitCode == 0 && build.Log.Contains("2000h words", StringComparison.Ordinal), template.Id + " real independent XC8 compilation under Unicode and spaces");
    var preview = await services.Builds.Pic.PreviewAsync(root);
    Check(preview.Bytes > 0 && (await File.ReadAllTextAsync(preview.HexPath)).StartsWith(':') &&
        (await File.ReadAllBytesAsync(preview.ElfPath)).Take(4).SequenceEqual(new byte[] { 0x7f, 0x45, 0x4c, 0x46 }), "HEX and ELF are actual compiler artifacts");
    Check(!Directory.GetFiles(root, "*.p1").Any() && File.Exists(Path.Combine(root, ".studiox/toolchain.lock.json")), "compiler intermediates isolated and tool content locked");
    Check(await services.Downloads.ConfigurationAsync(root) is null, "PIC cannot start an OpenOCD hardware session");
    var bytes = await File.ReadAllBytesAsync(preview.HexPath);
    await File.AppendAllTextAsync(preview.HexPath, "changed");
    await Reject("PIC_BUILD_STALE", () => services.Builds.Pic.PreviewAsync(root));
    await File.WriteAllBytesAsync(preview.HexPath, bytes);
    var header = Path.Combine(root, "include/probe.h");
    await File.WriteAllTextAsync(header, "/* source changed */\n");
    await Reject("PIC_BUILD_STALE", () => services.Builds.Pic.PreviewAsync(root));
    File.Delete(header);
    projects[template.Id] = root;
}
var basic = projects["xc8-basic"];
await Reject("PIC_CONFIGURE", () => services.Builds.ConfigureAsync(basic));
await Reject("PIC_BUILD_SETTINGS", () => services.Builds.SaveSettingsAsync(basic, new ProjectBuildSettings()));
var sourcePath = Path.Combine(basic, "src/main.c");
var original = await File.ReadAllTextAsync(sourcePath);
await File.WriteAllTextAsync(sourcePath, "#include <xc.h>\nvoid main(void) { this is not valid C; }\n");
var failed = await services.Builds.BuildAsync(basic);
await File.WriteAllTextAsync(Path.Combine(evidence, "invalid-c.log"), failed.Log);
Check(!failed.Success && failed.ExitCode != 0 && !File.Exists(Path.Combine(basic, ".studiox/pic-build.json")), "failed compilation preserves original diagnostic and revokes prior HEX export");
await Reject("PIC_BUILD_REQUIRED", () => services.Builds.Pic.PreviewAsync(basic));
await File.WriteAllTextAsync(sourcePath, original);
var changed = false;
await Reject("PIC_BUILD_CHANGED", () => services.Builds.BuildAsync(basic, output: new InlineProgress(text =>
{
    if (!changed && text.Contains("独立 XC8", StringComparison.Ordinal))
    {
        File.AppendAllText(sourcePath, "\n/* external edit during build */\n");
        changed = true;
    }
})));
Check(changed && !File.Exists(Path.Combine(basic, ".studiox/pic-build.json")), "concurrent source edits cannot receive fresh export credentials");
await File.WriteAllTextAsync(sourcePath, original);
await File.WriteAllTextAsync(Path.Combine(basic, "src/helper.c"), "unsigned char helper(void) { return 7; }\n");
Check((await services.Builds.BuildAsync(basic)).Success, "multiple C translation units build through XC8");
var manifestPath = Path.Combine(basic, ".studiox/project.json");
var manifest = await ProjectService.ReadAsync(basic);
await JsonStore.WriteAsync(manifestPath, manifest with { DeviceId = "PIC16F876A" });
await Reject("PROJECT_PIC", () => ProjectService.ReadAsync(basic));
await JsonStore.WriteAsync(manifestPath, manifest);
await using (var language = new CodeIntelligenceService(runtime, Path.Combine(evidence, "language-data")))
{
    var gpio = projects["xc8-gpio-4mhz"];
    await language.StartAsync(gpio);
    var text = "#include <xc.h>\nvoid main(void) { TRI";
    var completions = await language.CompleteAsync("src/main.c", text, text.Length);
    Check(completions.Any(item => item.Label.Contains("TRISD", StringComparison.Ordinal)), "real clangd completes official PIC register declarations");
    var valid = "#include <xc.h>\nvoid main(void) { TRISDbits.TRISD0 = 0; PORTDbits.RD0 = 1; CLRWDT(); }\n";
    await language.SynchronizeDiagnosticsAsync("src/main.c", valid, []);
    for (var attempt = 0; attempt < 60 && !language.GetDiagnostics().Any(batch => batch.Text == valid && batch.IsComplete); attempt++)
    {
        await Task.Delay(100);
    }
    await File.WriteAllTextAsync(Path.Combine(evidence, "diagnostics.json"), JsonSerializer.Serialize(language.GetDiagnostics(), new JsonSerializerOptions { WriteIndented = true }));
    await File.WriteAllLinesAsync(Path.Combine(evidence, "language.log"), language.DrainLog());
    Check(language.GetDiagnostics().Any(batch => batch.Text == valid && batch.IsComplete && batch.Items.All(item => item.Severity != 1)), "valid PIC register C has no false syntax errors in generic analysis");
}
await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new { passed = true, hardware = false, mplabIdeInvoked = false, checks, projects }, new JsonSerializerOptions { WriteIndented = true }));
