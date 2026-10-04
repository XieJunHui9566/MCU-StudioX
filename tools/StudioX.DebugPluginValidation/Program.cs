using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Plugins;
using StudioX.DebugPluginValidation;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Extensions;
using StudioX.Foundation;

if (args is [var hardwareMode, var hardwareSource, var toolRuntime, var pluginHost, var sampleArchive, var hardwareOutput] && hardwareMode is "--hardware" or "--hardware-reset")
{
    await HardwareChecks.RunAsync(hardwareSource, toolRuntime, pluginHost, sampleArchive, hardwareOutput, hardwareMode == "--hardware-reset");
    return;
}
if (args is not [var hostDirectory, var outputDirectory]) { throw new ArgumentException("Usage: <plugin-host directory> <new evidence directory>"); }
var root = Path.GetFullPath(outputDirectory);
if (Directory.Exists(root)) { throw new InvalidOperationException("Use a new evidence directory."); }
Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(bool passed, string label)
{
    if (!passed)
    {
        throw new InvalidOperationException(label);
    }
    checks.Add(label);
    Console.WriteLine("PASS " + label);
}
async Task WaitAsync(Func<bool> condition)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!condition())
    {
        await Task.Delay(15, deadline.Token);
    }
}
Check(OpenOcdDebugDiagnostics.TargetExaminationFailure("Error: [stm32f4x.cpu] Examination failed")?.Code == "DEBUG_TARGET_EXAMINE", "target examination failure has actionable code before GDB attach");
Check(OpenOcdDebugDiagnostics.TargetExaminationFailure("Warn : target stm32f4x.cpu examination failed")?.Message.Contains("examination failed") == true, "OpenOCD original examination diagnostic preserved");
Check(OpenOcdDebugDiagnostics.TargetExaminationFailure("Error: Target not examined yet") is not null &&
    OpenOcdDebugDiagnostics.TargetExaminationFailure("Info : Listening on port 3333 for gdb connections") is null &&
    OpenOcdDebugDiagnostics.TargetExaminationFailure("Info : previous examination failed; now recovered") is null, "startup classification distinguishes failures from listening and informational text");
await DebugPlanChecks.RunAsync(root, Check);
var runtime = Path.Combine(root, "runtime");
Directory.CreateDirectory(Path.Combine(runtime, "plugin-host"));
foreach (var file in Directory.EnumerateFiles(hostDirectory)) { File.Copy(file, Path.Combine(runtime, "plugin-host", Path.GetFileName(file))); }
var project = Path.Combine(root, "project");
Directory.CreateDirectory(Path.Combine(project, ".studiox"));
Directory.CreateDirectory(Path.Combine(project, "src"));
await JsonStore.WriteAsync(Path.Combine(project, ".studiox/project.json"), new ProjectManifest(1, "debug-fixture", "validation.pack", "1.0.0", new string('A', 64), "STM32F407ZGT6", "blank", "gcc", "1.0.0", "gcc"));
await File.WriteAllTextAsync(Path.Combine(project, F407DebugExample.RelativeFile), F407DebugExample.Source);
var plugin = Path.Combine(root, "plugin");
Directory.CreateDirectory(plugin);
var assembly = Assembly.GetExecutingAssembly().Location;
var assemblyName = Path.GetFileName(assembly);
File.Copy(assembly, Path.Combine(plugin, assemblyName));
var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(assembly)));
var manifest = new PluginManifest(1, 3, "validation.debug-view", "1.0.0", "Debug view", assemblyName,
    typeof(DebugFixturePlugin).FullName!, ["debugAdapters"], new()
    {
        [assemblyName] = hash
    }, HostTools: ["validation.wait"]);
await JsonStore.WriteAsync(Path.Combine(plugin, "plugin.json"), manifest);
var archive = Path.Combine(root, "fixture.studioxplugin");
await PluginRepository.PackAsync(plugin, archive);
await using var services = new WorkbenchService(runtime, Path.Combine(root, "data"));
await services.Debugger.OpenProjectAsync(project);
var installed = await services.PluginManager.ImportAsync(archive);
Check(!installed.Enabled, "install does not implicitly run debug plugins");
await services.PluginManager.SetEnabledAsync(manifest.Id, true);
var mode = "normal";
var calls = 0;
TaskCompletionSource<bool>? entered = null, release = null;
await using var workspace = await services.PluginManager.OpenWorkspaceAsync(project, async (_, _, _, token) =>
{
    Interlocked.Increment(ref calls);
    var currentRelease = release;
    entered?.TrySetResult(true);
    if (currentRelease is not null)
    {
        await currentRelease.Task;
    }
    return JsonSerializer.SerializeToElement(new
    {
        mode
    });
});
Check(workspace.Contributions.Count == 1, "API 3 debug plugin runs in actual separate host");
await using var view = new PluginDebugViewSession(workspace, services.Debugger, manifest.Id, "snapshot");
Check(view.Current.Panel is null && services.Debugger.State == DebugState.Disconnected && calls == 0, "opening view never starts debug or reads a device");
await services.Debugger.StartOfflineAsync();
await WaitAsync(() => view.Current.Panel is not null);
Check(view.Current.State == DebugState.Stopped && !view.Current.Hardware && view.Current.Status.Contains("离线模拟"), "stopped snapshot automatically renders with explicit simulation source");
var input = await services.Debugger.CapturePluginSnapshotAsync(project, 42);
Check(input.Revision == 42 && input.FormatVersion == 1 && input.Snapshot.GetProperty("registers").GetArrayLength() == 56, "snapshot contract carries revision and existing register data");
var other = await services.Debugger.CapturePluginSnapshotAsync(Path.Combine(root, "other"), 1);
Check(other.State == "Disconnected" && other.Snapshot.GetProperty("registers").GetArrayLength() == 0, "foreign project cannot receive current debug snapshot");
var initial = view.Current.Revision;
var callsBefore = calls;
for (var i = 0; i < 100; i++) { view.Refresh(); }
await WaitAsync(() => view.Current.Panel is not null);
Check(calls - callsBefore <= 2, "burst refresh uses bounded latest request instead of unbounded plugin calls");
initial = view.Current.Revision;
await services.Debugger.ExecuteAsync(DebugAction.StepOver);
Check(view.Current.Panel is null, "resume invalidates panel immediately before asynchronous stop");
await WaitAsync(() => view.Current.Panel is not null && view.Current.Revision > initial);
Check(services.Debugger.Snapshot.Frames[0].Line != F407DebugExample.Entry, "single step refreshes actual offline MI snapshot");
await services.Debugger.ExecuteAsync(DebugAction.Continue);
var running = await services.Debugger.CapturePluginSnapshotAsync(project, 43);
Check(view.Current.State == DebugState.Running && view.Current.Panel is null && running.Snapshot.GetProperty("registers").GetArrayLength() == 0, "running target exposes no stale register snapshot");
await services.Debugger.ExecuteAsync(DebugAction.Pause);
await WaitAsync(() => view.Current.Panel is not null);

entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
release = new(TaskCreationOptions.RunContinuationsAsynchronously);
view.Refresh();
await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
var lateRevision = view.Current.Revision;
await services.Debugger.ExecuteAsync(DebugAction.Continue);
release.TrySetResult(true);
release = null;
entered = null;
await Task.Delay(150);
Check(view.Current.State == DebugState.Running && view.Current.Panel is null && view.Current.Revision > lateRevision, "late response cannot revive a panel after resume");
await services.Debugger.ExecuteAsync(DebugAction.Pause);
await WaitAsync(() => view.Current.Panel is not null);
foreach (var invalidMode in new[] { "error", "unknown", "action" })
{
    mode = invalidMode;
    view.Refresh();
    await WaitAsync(() => view.Current.Diagnostic is not null);
    Check(view.Current.Panel is null && (invalidMode != "error" || view.Current.Diagnostic!.Contains("debug-fixture-original-error")), "reject and retain diagnosis: " + invalidMode);
}
mode = "normal";
view.Refresh();
await WaitAsync(() => view.Current.Panel is not null);
Check(view.Current.Diagnostic is null, "manual retry recovers after invalid or failed extension response");
entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
release = new(TaskCreationOptions.RunContinuationsAsynchronously);
await using (var closingView = new PluginDebugViewSession(workspace, services.Debugger, manifest.Id, "snapshot"))
{
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await closingView.DisposeAsync();
    release.TrySetResult(true);
    Check(closingView.Current.Panel is null && closingView.Current.Status.Contains("关闭"), "closing during in-flight interpretation cancels waiting and rejects late result");
}
entered = null;
release = null;
await services.Debugger.StopAsync();
Check(view.Current.Panel is null && view.Current.State == DebugState.Disconnected, "ending debug immediately clears extension results");
await services.Debugger.StartOfflineAsync();
await WaitAsync(() => view.Current.Panel is not null);
entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
release = new(TaskCreationOptions.RunContinuationsAsynchronously);
view.Refresh();
await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
await services.PluginManager.SetEnabledAsync(manifest.Id, false);
release.TrySetResult(true);
Check(view.Current.Panel is null && view.Current.Status.Contains("插件已停止"), "disabling during interpretation invalidates the active view");
await view.DisposeAsync();
Check(view.Current.Panel is null && view.Current.Status.Contains("关闭"), "closing view drains background worker and clears data");
await services.Debugger.StopAsync();
await services.PluginManager.SetEnabledAsync(manifest.Id, true);
await using var reopened = await services.PluginManager.OpenWorkspaceAsync(project, (_, _, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { mode = "normal" })));
await using var switched = new PluginDebugViewSession(reopened, services.Debugger, manifest.Id, "snapshot");
await services.Debugger.StartOfflineAsync();
await WaitAsync(() => switched.Current.Panel is not null);
await services.Debugger.OpenProjectAsync(null);
Check(switched.Current.Panel is null && switched.Current.State == DebugState.Disconnected, "project closing cancels current extension work without leaking another project");
Check(await File.ReadAllTextAsync(Path.Combine(project, F407DebugExample.RelativeFile)) == F407DebugExample.Source, "all debug extension checks preserve firmware source");
await JsonStore.WriteAsync(Path.Combine(root, "result.json"), new { success = true, hardware = false, checks });
Console.WriteLine("Evidence: " + root);
