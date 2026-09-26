using StudioX.Application;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;
using StudioX.Packages;

// 不创建 OpenOCD/GDB 进程，不使用 USB 或 socket。实际 MI 文本经过生产解析器与适配器。
// --stm32 为独立的配置矩阵：运行 OpenOCD noinit 解析和 GDB 批处理，不连接硬件。
// --rp2350 同样只做软件检查；--rp2350-hardware 显式执行已授权板卡的备份/下载/调试/恢复。
if (args is ["--disassembly"])
    return await DisassemblyChecks.RunStandaloneAsync();
if (args is ["--image-verification", var imageVerificationOutput])
    return await ImageVerificationChecks.RunAsync(imageVerificationOutput);
if (args is ["--stm32", var packDirectory, var stm32Runtime, var matrixOutput])
    return await Stm32TargetChecks.RunAsync(packDirectory, stm32Runtime, matrixOutput);
if (args is ["--ag32", var agPack, var agRuntime, var agOutput])
    return await Ag32TargetChecks.RunAsync(agPack, agRuntime, agOutput);
if (args is ["--ag32-verify-diagnostics", var agDiagnosticOutput])
    return await Ag32TargetChecks.RunVerificationDiagnosticsAsync(agDiagnosticOutput);
if (args is ["--ag32-logic", var logicPack, var logicOutput])
    return await Ag32LogicProjectChecks.RunAsync(logicPack, logicOutput);
if (args is ["--wch", var wchPack, var wchRuntime, var wchOutput])
    return await WchTargetChecks.RunAsync(wchPack, wchRuntime, wchOutput);
if (args is ["--rp2350", var rpPack, var rpRuntime, var rpOutput])
    return await Rp2350TargetChecks.RunAsync(rpPack, rpRuntime, rpOutput);
if (args is ["--rp2350-hardware", var rpHwPack, var rpHwRuntime, var rpHwOutput, var rpSerial])
    return await Rp2350HardwareChecks.RunAsync(rpHwPack, rpHwRuntime, rpHwOutput, rpSerial);
if (args is not [var packArchive, var runtime, var outputDirectory]) return 2;
var root = Path.GetFullPath(outputDirectory);
if (Directory.Exists(root)) throw new InvalidOperationException("Use a new validation directory.");
Directory.CreateDirectory(root);
var passed = new List<string>();
void Pass(string message) { passed.Add(message); Console.WriteLine("PASS " + message); }
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
async Task Reject(Func<Task> action, string code)
{
    try { await action(); throw new InvalidOperationException("Expected " + code); }
    catch (StudioXException ex) when (ex.Code == code) { }
}
async Task Wait(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
    while (!condition()) await Task.Delay(15, timeout.Token);
}
var parsed = MiRecord.Parse("17^done,stack=[frame={level=\"0\",func=\"main\",args=[{name=\"x\",value=\"a\\\"b\\n\"}]},frame={level=\"1\",func=\"启动\"}]");
Check(parsed.Token == 17 && parsed.Data.Get("stack")!.Values.Count() == 2 && parsed.Data.Get("stack")!.Values.First().Get("args")!.Values.First().String("value") == "a\"b\n", "MI nested/duplicate fields");
var quoted = "E:/有 空格/firmware \"x\".elf\n";
Check(MiRecord.Parse("~" + MiRecord.Quote(quoted)).Data.Text == quoted, "MI quoting");
try { MiRecord.Parse("1^done,stack=[{bad=\"x\""); throw new InvalidOperationException("Expected bad MI"); } catch (FormatException) { }
Pass("MI nested lists, duplicate frame fields, escaped strings, Unicode paths, malformed input");
await DisassemblyChecks.RunAdapterAsync(Pass);

var repository = new PackRepository(Path.Combine(root, "packs"));
await repository.ImportAsync(packArchive);
var project = await DebugSessionService.CreateExampleAsync(repository, root, Path.Combine(root, "no-packages"));
await File.WriteAllTextAsync(Path.Combine(root, "project-path.txt"), project);
await using var session = new DebugSessionService(Path.Combine(root, "user-data"));
var output = new List<string>(); session.Output += text => { lock (output) output.Add(text); };
await session.OpenProjectAsync(project);
await Reject(() => session.ExecuteAsync(DebugAction.Continue), "DEBUG_STATE");
await Reject(() => session.ReadDisassemblyAsync(), "DEBUG_STATE");
await session.ToggleBreakpointAsync(F407DebugExample.RelativeFile, F407DebugExample.Call);
await session.ToggleBreakpointAsync(F407DebugExample.RelativeFile, 2);
await session.StartOfflineAsync();
Check(session.State == DebugState.Stopped && session.Snapshot.Frames[0].Line == F407DebugExample.Entry, "Entry pause");
await Reject(() => session.StartOfflineAsync(), "DEBUG_STATE");
Check(session.State == DebugState.Stopped && session.IsActive, "Duplicate start must not destroy the active session");
Check(session.Snapshot.Registers.Length == 56 && session.Snapshot.Registers.Single(r => r.Name == "pc").Value.StartsWith("0x0800", StringComparison.Ordinal), "Cortex-M4 register model");
Check(session.Breakpoints.Single(b => b.Line == 2).Verified == false && session.Breakpoints.Single(b => b.Line == F407DebugExample.Call).Verified, "Pending/executable distinction");
Pass("F407 offline start, main entry, core/FPU registers, pending breakpoint status");
await DisassemblyChecks.CheckStoppedSessionAsync(session, Pass);
await session.ToggleBreakpointAsync(F407DebugExample.RelativeFile, F407DebugExample.Entry - 1);
var relocated = session.Breakpoints.Single(b => b.Line == F407DebugExample.Entry - 1);
Check(relocated.Verified && relocated.BoundLocation is { } bound && bound.File == F407DebugExample.RelativeFile && bound.Line == F407DebugExample.Entry,
    "Resolved breakpoint location must not overwrite the requested source line");
await session.ChangeBreakpointAsync(relocated.Id, null);
Pass("GDB breakpoint relocation retains the requested line and exposes the actual binding line");
await session.ExecuteAsync(DebugAction.Continue);
await Reject(() => session.ReadMemoryAsync(0x20000000), "DEBUG_STATE");
await Reject(() => session.ReadDisassemblyAsync(), "DEBUG_STATE");
await Reject(() => session.ToggleBreakpointAsync(F407DebugExample.RelativeFile, 5), "DEBUG_STATE");
await Wait(() => session.State == DebugState.Stopped);
Check(session.Reason.Contains("命中断点", StringComparison.Ordinal) && session.Snapshot.Frames[0].Line == F407DebugExample.Call, "Run to breakpoint");
Check(session.Snapshot.Watches.Single(v => v.Name == "app_counter").Value == "1", "Counter from model");
Check(session.Snapshot.Registers.Any(r => r.Changed), "Changed register highlight");
Pass("Run/hit breakpoint, running-state read guards, change detection");
await session.ExecuteAsync(DebugAction.StepInto); await Wait(() => session.State == DebugState.Stopped);
Check(session.Snapshot.Frames.Length == 2 && session.Snapshot.Frames[0].Function == "ComputeOutput" && session.Snapshot.Locals.Any(v => v.Name == "input"), "Step into/locals");
await session.RefreshAsync(1); Check(session.Snapshot.SelectedFrame == 1 && session.Snapshot.Locals.Length == 0, "Select caller");
await DisassemblyChecks.CheckCallerSelectionAsync(session, Pass);
await session.RefreshAsync(0);
await session.ExecuteAsync(DebugAction.StepOver); await Wait(() => session.State == DebugState.Stopped);
Check(session.Snapshot.Locals.Single(v => v.Name == "doubled").Value == "2", "Local update");
await session.ExecuteAsync(DebugAction.StepOut); await Wait(() => session.State == DebugState.Stopped);
Check(session.Snapshot.Frames.Length == 1 && session.Snapshot.Watches.Single(v => v.Name == "app_output").Value == "3", "Finish result");
await Reject(() => session.ExecuteAsync(DebugAction.StepOut), "DEBUG_STATE");
Pass("Step into/over/out, stack selection, locals, outermost-frame guard");
await session.ChangeWatchAsync("missing_symbol", false);
Check(session.Snapshot.Watches.Single(v => v.Name == "missing_symbol").Value.StartsWith("不可用", StringComparison.Ordinal), "Invalid expression display");
await Reject(() => session.ChangeWatchAsync("app_counter = 9", false), "DEBUG_STATE");
await Reject(() => session.ChangeWatchAsync("function()", false), "DEBUG_STATE");
var memory = await session.ReadMemoryAsync(0x20000000);
Check(memory.StartsWith("010000000100000003000000", StringComparison.Ordinal), "Little-endian memory");
await Reject(() => session.ReadMemoryAsync(0x40000000), "GDB_COMMAND");
Pass("Watch errors, side-effect rejection, read-only memory and invalid-address errors");
foreach (var point in session.Breakpoints.ToArray()) await session.ChangeBreakpointAsync(point.Id, false);
await session.ExecuteAsync(DebugAction.Continue); await Wait(() => session.State == DebugState.Running);
await session.ExecuteAsync(DebugAction.Pause); await Wait(() => session.State == DebugState.Stopped);
await session.ExecuteAsync(DebugAction.Reset); await Wait(() => session.Reason.Contains("复位", StringComparison.Ordinal));
Check(session.Snapshot.Watches.Single(v => v.Name == "app_counter").Value == "0", "Reset values");
await session.ExecuteAsync(DebugAction.Continue); await session.StopAsync(); await Task.Delay(180);
Check(session.State == DebugState.Disconnected && session.Snapshot.Registers.Length == 0, "No stale stop event");
await Reject(() => session.ReadDisassemblyAsync(), "DEBUG_STATE");
Pass("Disable breakpoint, pause, reset and terminate while running; late events ignored");
var saved = session.Breakpoints.ToArray();
await session.OpenProjectAsync(null); Check(session.Breakpoints.Count == 0, "Close clears points");
await session.OpenProjectAsync(project); Check(session.Breakpoints.SequenceEqual(saved) && session.Watches.Contains("missing_symbol"), "Settings restore");
await session.UpdateLinesAsync(new Dictionary<string, int> { [saved[0].Id] = saved[0].Line + 1 });
await session.OpenProjectAsync(project); Check(session.Breakpoints[0].Line == saved[0].Line + 1, "Edited line persisted");
Pass("Project isolation, breakpoint/watch persistence, edited line persistence");
foreach (var point in session.Breakpoints.ToArray()) await session.ChangeBreakpointAsync(point.Id, null);
await session.StartOfflineAsync();
foreach (var line in F407DebugExample.ExecutableLines) await session.ToggleBreakpointAsync(F407DebugExample.RelativeFile, line);
Check(session.Breakpoints.Count(b => b.Verified) == 6 && session.Breakpoints.Count(b => !b.Verified) == 1, "Hardware breakpoint resource error");
var unbound = session.Breakpoints.Single(b => !b.Verified);
await session.ChangeBreakpointAsync(session.Breakpoints.First(b => b.Verified).Id, false);
await session.ChangeBreakpointAsync(unbound.Id, true);
Check(session.Breakpoints.Single(b => b.Id == unbound.Id).Verified, "Retry binding after releasing a hardware slot");
await session.StopAsync();
Pass("Hardware breakpoint capacity, failed binding status and retry after freeing a slot");
var downloads = new OpenOcdService(new ToolsetCatalog(Path.Combine(runtime, "toolsets")));
var configuration = (await downloads.ConfigurationAsync(project))!;
var manifest = await JsonStore.ReadAsync<ToolsetManifest>(Path.Combine(runtime, "toolsets/arm.gnu/1.0.0/toolset.json"));
var tools = new ResolvedToolset(manifest, Path.Combine(runtime, "toolsets/arm.gnu/1.0.0"), "offline-plan-only");
await ProbePlanChecks.RunAsync(project, configuration, tools, downloads, root, Pass);
await SpecialBreakpointChecks.RunAsync(session, project, Pass);
lock (output) File.WriteAllLines(Path.Combine(root, "mi-transcript.txt"), output);
await File.WriteAllLinesAsync(Path.Combine(root, "result.txt"), passed.Prepend("PASS — offline only; no chip connected or accessed"));
return 0;
