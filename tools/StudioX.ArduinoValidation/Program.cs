using System.Text.Json;
using StudioX.Application.Arduino;
using StudioX.Application.CodeIntelligence;
using StudioX.Devices;
using StudioX.Engine;
using StudioX.Engine.Arduino;
using StudioX.Foundation;

if (args is ["--uploader-fixture", var fixturePort, var fixtureHex, var fixtureMode])
{
    // 仅隔离工具组件的上传 recipe 调用此替身；不访问串口，仍通过真实 CLI 执行完整上传路径。
    await ArduinoUploadChecks.RunUploaderAsync(fixturePort, fixtureHex, fixtureMode);
    return;
}
if (args is ["--upload-only", var uploadEvidence, var uploadRuntime])
{
    await ArduinoUploadChecks.RunAsync(Path.GetFullPath(uploadEvidence), Path.GetFullPath(uploadRuntime));
    return;
}
if (args is ["--hardware-upload", var hardwareEvidence, var hardwareProject, var hardwareRuntime, var hardwareData,
    var hardwarePort, var approvedBoard, var approvedSha256])
{
    Directory.CreateDirectory(hardwareEvidence);
    var hardwareBuilds = new BuildService(new ToolsetCatalog(Path.Combine(hardwareRuntime, "toolsets")), userDataDirectory: hardwareData);
    await using var hardwareDevices = new DeviceHub();
    var hardwareService = new ArduinoService(hardwareBuilds, hardwareDevices);
    var hardwarePreview = await hardwareService.PreviewUploadAsync(hardwareProject);
    if (hardwarePreview.Board.Fqbn != approvedBoard || hardwarePreview.Sha256 != approvedSha256)
    {
        throw new InvalidOperationException("Current board or firmware differs from the explicitly approved image.");
    }
    Console.WriteLine(JsonSerializer.Serialize(hardwarePreview));
    var hardwareReport = await hardwareService.UploadApprovedAsync(hardwareProject, hardwarePort, hardwarePreview.ApprovalId,
        new InlineProgress(Console.Write));
    await File.WriteAllTextAsync(Path.Combine(hardwareEvidence, "hardware-upload.json"), JsonSerializer.Serialize(hardwareReport,
        new JsonSerializerOptions { WriteIndented = true }));
    if (!hardwareReport.Success)
    {
        Environment.ExitCode = 1;
    }
    return;
}

if (args is ["--language-only", var languageEvidence, var languageRuntime, var languageProject, var languageData])
{
    Directory.CreateDirectory(languageEvidence);
    await using var language = new CodeIntelligenceService(Path.GetFullPath(languageRuntime), Path.GetFullPath(languageData));
    await language.StartAsync(languageProject);
    var source = "#include <Arduino.h>\nvoid setup() { pinM";
    var items = await language.CompleteAsync("Blink_uno.ino", source, source.Length);
    await language.SynchronizeDiagnosticsAsync("Blink_uno.ino", source, []);
    await Task.Delay(1000);
    await File.WriteAllTextAsync(Path.Combine(languageEvidence, "language.json"), JsonSerializer.Serialize(new
    {
        items,
        diagnostics = language.GetDiagnostics(),
        log = language.DrainLog().ToArray()
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Language returned {items.Count} completions");
    return;
}

var evidence = Path.GetFullPath(args[0]);
var runtime = Path.GetFullPath(args[1]);
Directory.CreateDirectory(evidence);
var checks = new List<string>();
void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
    checks.Add(message);
    Console.WriteLine("PASS " + message);
}

async Task Reject(Func<Task> action, string code, string message)
{
    try
    {
        await action();
        throw new InvalidOperationException("Expected " + code + ": " + message);
    }
    catch (StudioXException error) when (error.Code == code)
    {
        Check(true, message);
    }
}

var catalog = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
Check(!File.Exists(Path.Combine(runtime, "toolsets", ArduinoProjectSettings.ToolsetId,
    ArduinoProjectSettings.CoreVersion, "seed", "inventory.yaml")), "portable component excludes developer installation identity");
var builds = new BuildService(catalog, userDataDirectory: Path.Combine(evidence, "user-data"));
await using var devices = new DeviceHub();
var application = new ArduinoService(builds, devices);
foreach (var reserved in new[] { "CON", "NUL", "COM3", "AUX" })
{
    await Reject(async () => { await application.CreateAsync(reserved, "uno", Path.Combine(evidence, reserved)); },
        "PATH_UNSAFE", "project creation rejects Windows device name " + reserved + " before file access");
}
var projects = new List<string>();
foreach (var board in ArduinoBoard.Supported.Where(board => board.PlatformId == "avr"))
{
    var name = "Blink_" + board.Id.Replace('-', '_');
    var root = Path.Combine(evidence, "中文 parent with spaces", name);
    var created = await application.CreateAsync(name, board.Id, root);
    Check(created == await ProjectService.ReadAsync(root), board.Id + " manifest round trip and precise target");
    Check(!File.Exists(Path.Combine(root, "CMakeLists.txt")), board.Id + " preserves native sketch layout");
    var report = await builds.BuildAsync(root);
    await File.WriteAllTextAsync(Path.Combine(evidence, board.Id + "-compile.log"), report.Log);
    Check(report.Success && report.ExitCode == 0, board.Id + " real AVR compile/link under Unicode and spaces");
    Check(report.Artifacts.Any(path => path.EndsWith(".elf")), board.Id + " emits real ELF");
    var preview = await application.PreviewUploadAsync(root);
    Check(preview.Bytes > 0 && preview.Board == board, board.Id + " offline upload preview without opening serial");
    Check(await new OpenOcdService(catalog).ConfigurationAsync(root) is null, board.Id + " never routes to OpenOCD");
    projects.Add(root);
}
var uno = projects[0];
var entry = Path.Combine(uno, "Blink_uno.ino");
var originalSource = await File.ReadAllTextAsync(entry);
await File.AppendAllTextAsync(entry, "\n// changed after compilation\n");
await Reject(async () => { await application.PreviewUploadAsync(uno); }, "ARDUINO_BUILD_STALE", "source changes invalidate upload");
await File.WriteAllTextAsync(entry, originalSource);
var previewUno = await application.PreviewUploadAsync(uno);
var image = await File.ReadAllBytesAsync(previewUno.HexPath);
await File.AppendAllTextAsync(previewUno.HexPath, "changed");
await Reject(async () => { await application.PreviewUploadAsync(uno); }, "ARDUINO_BUILD_STALE", "HEX changes invalidate upload");
await File.WriteAllBytesAsync(previewUno.HexPath, image);
await Reject(async () => { await application.UploadApprovedAsync(uno, "COM3 & calc", previewUno.ApprovalId); }, "ARDUINO_PORT", "reject injected or ambiguous port before any hardware action");
using (await devices.ReserveAsync("serial:COM3"))
{
    await Reject(async () => { await application.UploadApprovedAsync(uno, "com3", previewUno.ApprovalId); }, "DEVICE_OWNED", "upload respects existing serial ownership without launching uploader");
}
await Reject(async () => { await builds.Arduino.UploadApprovedAsync(uno, "COM3", "unapproved"); }, "ARDUINO_UPLOAD_CHANGED", "changed approval rejected before uploader starts");
Check((await builds.BuildAsync(uno)).Success, "rebuild remains valid before generation approval check");
var rebuiltPreview = await application.PreviewUploadAsync(uno);
Check(rebuiltPreview.Sha256 == previewUno.Sha256 && rebuiltPreview.ApprovalId != previewUno.ApprovalId, "same HEX bytes from a new build require renewed approval");
await Reject(async () => { await builds.Arduino.UploadApprovedAsync(uno, "COM3", previewUno.ApprovalId); }, "ARDUINO_UPLOAD_CHANGED", "approval cannot transfer to a later build even when HEX matches");
await File.WriteAllTextAsync(entry, originalSource + "\nintentional_compile_failure\n");
var failure = await builds.BuildAsync(uno);
Check(!failure.Success && failure.ExitCode != 0 && failure.Log.Contains("intentional_compile_failure"), "failed compile retains raw tool diagnostic and exit code");
Check(!File.Exists(Path.Combine(uno, ArduinoBuildReceipt.RelativePath)), "failed compile removes previous upload receipt");
await File.WriteAllTextAsync(entry, originalSource);
await Reject(async () => { await application.PreviewUploadAsync(uno); }, "ARDUINO_BUILD_REQUIRED", "failed build cannot reuse older HEX");
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try
    {
        await builds.BuildAsync(uno, cancellationToken: cancellation.Token);
        throw new InvalidOperationException("Cancellation ignored");
    }
    catch (OperationCanceledException)
    {
        Check(true, "pre-cancelled build starts no compiler");
    }
}
var importRoot = Path.Combine(evidence, "ImportedSketch");
await File.WriteAllTextAsync(Path.Combine(uno, "sketch.yaml"), "default_profile: external\n");
await Reject(async () => { await builds.BuildAsync(uno); }, "ARDUINO_PROFILE", "external sketch profile cannot silently override fixed dependencies");
await application.CreateAsync("ImportedSketch", "uno", importRoot, uno);
Check(File.Exists(Path.Combine(importRoot, ".studiox/import/sketch.yaml")) && !File.Exists(Path.Combine(importRoot, "sketch.yaml")), "import preserves external profile as inactive provenance");
File.Delete(Path.Combine(uno, "sketch.yaml"));
Check(await File.ReadAllTextAsync(Path.Combine(importRoot, "ImportedSketch.ino")) == originalSource, "import copies main sketch to matching new filename");
Check(await File.ReadAllTextAsync(entry) == originalSource, "import preserves original source");
await Reject(async () => { await application.CreateAsync("Nested", "uno", Path.Combine(uno, "Nested"), uno); }, "ARDUINO_IMPORT_TARGET", "import rejects nested self-copy destination");
await Reject(async () => { await application.CreateAsync("Invalid_Name", "uno-r4", Path.Combine(evidence, "Invalid_Name")); }, "ARDUINO_BOARD", "unsupported boards are never guessed");
await Reject(async () => { await application.InstallLibraryAsync(uno, "Wire --git-url", "latest"); }, "ARDUINO_LIBRARY", "library manager requires safe name and exact version");
Check(ArduinoBuildService.ValidatePort(" com12 ") == "COM12", "port normalization shares ownership key");
var search = await application.SearchLibrariesAsync(uno, "Adafruit AHTX0");
Check(search.Any(item => item.Name == "Adafruit AHTX0"), "search reads official Arduino library index");
var installedLibraries = await application.InstallLibraryAsync(uno, "Adafruit AHTX0", "2.0.5");
Check(installedLibraries.Contains(new ArduinoLibrary("Adafruit AHTX0", "2.0.5")) && installedLibraries.Any(item => item.Name == "Adafruit BusIO"), "install pins requested version and transitive dependencies");
var sensorSource = "#include <Adafruit_AHTX0.h>\nAdafruit_AHTX0 aht;\nvoid setup() { Serial.begin(115200); aht.begin(); }\nvoid loop() { sensors_event_t humidity, temperature; aht.getEvent(&humidity, &temperature); Serial.println(temperature.temperature); delay(1000); }\n";
await File.WriteAllTextAsync(entry, sensorSource);
var sensorBuild = await builds.BuildAsync(uno);
await File.WriteAllTextAsync(Path.Combine(evidence, "sensor-library-compile.log"), sensorBuild.Log);
Check(sensorBuild.Success, "real AVR compile/link with third party sensor library");
var librarySettings = (await ProjectService.ReadAsync(uno)).Arduino!;
var libraryRoot = new ArduinoRuntime(catalog, Path.Combine(evidence, "user-data")).LibraryRoot(librarySettings);
var header = ArduinoSourceSnapshot.Files(libraryRoot).First(file => file.EndsWith(".h", StringComparison.Ordinal));
await File.AppendAllTextAsync(Path.Combine(libraryRoot, header), "\n// external edit\n");
await Reject(async () => { await application.PreviewUploadAsync(uno); }, "ARDUINO_DEPENDENCY_CHANGED", "changed dependency bytes invalidate upload despite matching library version");
await application.RestoreLibrariesAsync(uno);
Check((await builds.BuildAsync(uno)).Success, "restore exact library set preserves source identity and compiles");
var raceRoot = Path.Combine(evidence, "RaceSketch");
await application.CreateAsync("RaceSketch", "uno", raceRoot);
var raceBuilds = new BuildService(catalog, userDataDirectory: Path.Combine(evidence, "race-user"));
var raceApplication = new ArduinoService(raceBuilds, devices);
var raceManifest = await ProjectService.ReadAsync(raceRoot);
var reachedPreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var continuePreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var installing = raceApplication.InstallLibraryAsync(raceRoot, "Adafruit BusIO", "1.17.4", new InlineProgress(message =>
{
    if (!message.StartsWith("首次准备", StringComparison.Ordinal))
    {
        return;
    }
    reachedPreparation.TrySetResult();
    continuePreparation.Task.GetAwaiter().GetResult();
}));
try
{
    var intercepted = await Task.WhenAny(reachedPreparation.Task, installing, Task.Delay(30000));
    if (intercepted == installing)
    {
        await installing;
    }
    Check(intercepted == reachedPreparation.Task && !installing.IsCompleted, "concurrent configuration fixture intercepts initial tool preparation");
    await JsonStore.WriteAsync(Path.Combine(raceRoot, ".studiox/project.json"), raceManifest with
    {
        DeviceId = ArduinoBoard.Require("nano").Fqbn,
        Arduino = new("nano", [])
    });
}
finally
{
    continuePreparation.TrySetResult();
}
await Reject(async () => { await installing; }, "ARDUINO_PROJECT_CHANGED", "library installation rejects configuration changes during tool preparation");
Check((await ProjectService.ReadAsync(raceRoot)).Arduino!.BoardId == "nano", "library transaction preserves external board selection");
if (File.Exists(Path.Combine(runtime, "languages/clangd/bin/clangd.exe")))
{
    await using var intelligence = new CodeIntelligenceService(runtime, Path.Combine(evidence, "user-data"));
    await intelligence.StartAsync(uno);
    var completionText = "#include <Arduino.h>\nvoid setup() { pinM";
    var completions = await intelligence.CompleteAsync("Blink_uno.ino", completionText, completionText.Length);
    await File.WriteAllTextAsync(Path.Combine(evidence, "language-completion.json"), JsonSerializer.Serialize(completions));
    await File.WriteAllTextAsync(Path.Combine(evidence, "language.log"), string.Join('\n', intelligence.DrainLog()));
    Check(completions.Any(item => item.FilterText == "pinMode"), "real clangd completes Arduino API in ino");
    var hoverText = "#include <Arduino.h>\nvoid setup() { pinMode(13, OUTPUT); }\nvoid loop() {}\n";
    var hover = await intelligence.HoverAsync("Blink_uno.ino", hoverText, hoverText.IndexOf("pinMode", StringComparison.Ordinal) + 2);
    Check(hover?.Contents.Contains("pinMode", StringComparison.Ordinal) == true, "Arduino API hover resolves real core declaration");
    await intelligence.SynchronizeDiagnosticsAsync("Blink_uno.ino", hoverText, []);
    for (var attempt = 0; attempt < 40 && !intelligence.GetDiagnostics().Any(batch => batch.Text == hoverText); attempt++)
    {
        await Task.Delay(50);
    }
    Check(intelligence.GetDiagnostics().Any(batch => batch.Text == hoverText && batch.IsComplete && batch.Items.All(item => item.Severity != 1)), "valid Arduino ino has no false core/header errors");
    await File.WriteAllTextAsync(Path.Combine(evidence, "language.log"), string.Join('\n', intelligence.DrainLog()));
}
else
{
    throw new InvalidOperationException("Real clangd runtime required for Arduino language acceptance.");
}
await File.WriteAllTextAsync(Path.Combine(evidence, "arduino-validation.json"), JsonSerializer.Serialize(new
{
    passed = true,
    realCompilation = "passed",
    hardware = "not_requested",
    boards = ArduinoBoard.Supported.Where(board => board.PlatformId == "avr").Select(board => board.Fqbn),
    checks
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Passed {checks.Count} Arduino checks; hardware not requested.");
