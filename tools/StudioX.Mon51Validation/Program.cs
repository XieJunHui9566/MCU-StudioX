using StudioX.Application;
using StudioX.Application.StcDebugging;
using StudioX.Devices;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

if (args is ["--installed-setup-pack", var installedRuntime, var installedData, var installedProject, var installedCatalog, var installedOutput])
{
    return await InstalledSetupChecks.RunAsync(installedRuntime, installedData, installedProject, installedCatalog, installedOutput);
}

if (args is ["--setup-pack-offline", var monitorPack, var runtime, var setupProject, var setupOutput])
{
    return await SetupChecks.RunAsync(Path.GetFullPath(monitorPack), Path.GetFullPath(runtime), Path.GetFullPath(setupProject), Path.GetFullPath(setupOutput));
}

if (args is ["--hardware", var port, var hardwareOutput])
{
    return await HardwareChecks.RunAsync(port, Path.GetFullPath(hardwareOutput));
}
if (args is ["--hardware-source", var sourcePort, var sourceHardwareOutput])
{
    return await HardwareSourceChecks.RunAsync(sourcePort, Path.GetFullPath(sourceHardwareOutput));
}
if (args is ["--built-symbols", var builtProject, var builtOutput])
{
    return await BuiltSymbolsChecks.RunAsync(Path.GetFullPath(builtProject), Path.GetFullPath(builtOutput));
}
if (args is ["--download-offline", var downloadProject, var downloadOutput])
{
    return await DownloadChecks.RunAsync(Path.GetFullPath(downloadProject), Path.GetFullPath(downloadOutput));
}
if (args is not [var output]) { Console.Error.WriteLine("Usage: StudioX.Mon51Validation <new-output-directory>"); return 2; }
var root = Path.GetFullPath(output);
Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(bool condition, string name) { if (!condition) { throw new InvalidOperationException(name); } checks.Add(name); }
async Task Reject(Func<Task> action, string code, string name)
{
    try
    {
        await action();
        throw new InvalidOperationException("Accepted: " + name);
    }
    catch (StudioXException ex) when (ex.Code == code) { checks.Add(name); }
}
var project = Path.Combine(root, "project");
Directory.CreateDirectory(Path.Combine(project, ".studiox"));
await JsonStore.WriteAsync(Path.Combine(project, ".studiox", "project.json"), new ProjectManifest(1, "Mon51Probe", "stc.mon51", "1.0.0", "", "stc.IAP15F2K61S2", "bare-metal", "stc.sdcc", "4.5.0", "sdcc-4.5.0-15242"));
var request = Mon51Client.Encode([8, 0]);
Check(request.SequenceEqual(new byte[] { 0xff, 0x5a, 3, 8, 0, 0x9c }), "compact GO frame 08 00 checksum");
await using var hub = new DeviceHub();
var coldFixture = new Mon51OfflineTransport();
await using (var coldSession = new Mon51DebugSession(Path.Combine(root, "cold-start")))
{
    await coldSession.ConnectAsync(hub, coldFixture, project);
    Check(coldSession.State == DebugState.Stopped && coldFixture.Commands[0].SequenceEqual(new byte[] { 0, 0 })
        && !coldFixture.Commands.Any(c => c.SequenceEqual(new byte[] { 0, 2 })),
        "cold monitor attaches before sending any pause frame");
    await coldSession.StopAsync();
}
var resetFixture = new Mon51OfflineTransport(initialPc: 0);
resetFixture.Code[0xdbfd] = 0x02;
await using (var resetSession = new Mon51DebugSession(Path.Combine(root, "cold-reset-vector")))
{
    await resetSession.ConnectAsync(hub, resetFixture, project);
    Check(resetSession.State == DebugState.Stopped && resetSession.Pc == 0xdbfd &&
        !resetFixture.Commands.Any(c => c[0] is 6 or 8 || c is [2, 5, ..]),
        "cold PC=0 is mapped by native SET_PC to user trampoline before any execution, erase or CODE write");
    await resetSession.ExecuteAsync(DebugAction.Continue);
    Check(resetFixture.Running && resetFixture.Pc == 0xdbfd,
        "cold continue runs from the user trampoline instead of the physical monitor reset vector");
    await resetSession.StopAsync();
}
var runningFixture = new Mon51OfflineTransport(running: true);
await using (var runningSession = new Mon51DebugSession(Path.Combine(root, "running-attach")))
{
    await runningSession.ConnectAsync(hub, runningFixture, project);
    Check(runningSession.State == DebugState.Stopped && !runningFixture.Running
        && runningFixture.Commands.Take(3).Select(c => Convert.ToHexString(c)).SequenceEqual(new[] { "0000", "0002", "0000" }),
        "first attach to running firmware pauses only after initial hello times out");
    await runningSession.StopAsync();
}
var silent = new Mon51OfflineTransport { Silent = true };
var retryFixture = new Mon51OfflineTransport();
var attempts = 0;
await using (var retryDebug = new DebugSessionService(Path.Combine(root, "retry-user-data"), hub,
    _ => attempts++ == 0 ? silent : retryFixture))
{
    await retryDebug.OpenProjectAsync(project);
    try
    {
        await retryDebug.StartMon51Async("COM14", 115200, "IAP15F2K61S2");
        throw new InvalidOperationException("Silent transport accepted");
    }
    catch (StudioXException error) when (error.Code == "MON51_TIMEOUT")
    {
        Check(error.Message.Contains("未收到任何应答") && !error.Message.Contains("校验失败"), "zero RX is reported as no response rather than checksum failure");
    }
    Check(!retryDebug.IsActive && retryDebug.MonitorSession is null && retryDebug.State == DebugState.Faulted,
        "failed connection clears facade session and releases ownership for retry");
    Check(File.ReadAllText(retryDebug.SessionLogPath!).Contains("TIMEOUT"), "failed connection retains original timeout log");
    await retryDebug.StartMon51Async("COM14", 115200, "IAP15F2K61S2");
    Check(retryDebug.IsActive && retryDebug.State == DebugState.Stopped, "same facade reconnects without a separate stop after initial failure");
    await retryDebug.StopAsync();
}
var fixture = new Mon51OfflineTransport();
await using var debug = new DebugSessionService(Path.Combine(root, "user-data"), hub, _ => fixture);
await debug.OpenProjectAsync(project);
await Reject(() => debug.StartMon51Async("COM14", 115200, "STC15F2K60S2"), "MON51_TARGET", "unverified target rejected before opening transport");
await debug.StartMon51Async("COM14", 115200, "IAP15F2K61S2");
var monitor = debug.MonitorSession!;
await monitor.SetPreferencesAsync(new[] { new SourceBreakpoint("pending", "src/main.c", 10) }, debug.Watches);
Check(!debug.Breakpoints.Single().Verified && debug.Breakpoints.Single().Message!.Contains("构建回执"), "saved unbound breakpoint carries missing build reason instead of a null success-looking state");
await Reject(() => debug.ExecuteAsync(DebugAction.Continue), "MON51_BREAKPOINT_UNBOUND", "continue with an enabled unbound source request never sends GO");
Check(debug.State == DebugState.Stopped && !fixture.Commands.Any(c => c.SequenceEqual(new byte[] { 8, 0 })), "unbound continue leaves target paused");
await monitor.ChangeSourceBreakpointAsync("pending", null);
Check(debug.IsActive && !debug.IsHardware && debug.State == DebugState.Stopped && monitor.IsSimulated, "facade shares active state, fixture visibly simulated");
await Reject(async () => { using var conflict = await hub.ReserveAsync(fixture.Key); }, "DEVICE_OWNED", "single connection owner enforced");
Check(debug.Snapshot.Registers.Single(r => r.Name == "A").Value == "0x11" && debug.Snapshot.Registers.Single(r => r.Name == "B").Value == "0x22" && debug.Snapshot.Registers.Single(r => r.Name == "DPTR").Value == "0x1234" && debug.Snapshot.Registers.Single(r => r.Name == "PSW").Value == "0x84" && debug.Snapshot.Registers.Single(r => r.Name == "SP").Value == "0x09", "19-byte register map and fragmented replies");
foreach (var space in new[] { Mon51MemorySpace.DataSfr, Mon51MemorySpace.Idata, Mon51MemorySpace.Xdata })
{
    Check((await monitor.ReadMemoryAsync(space, 0x20, 1))[0] == (byte)((byte)space * 16), "memory space " + space);
}
await Reject(() => monitor.ReadMemoryAsync(Mon51MemorySpace.Xdata, 0x400, 1), "MON51_RANGE", "monitor XDATA reservation rejected");
await Reject(() => monitor.AddBreakpointAsync(0xdbfd), "MON51_BREAKPOINT_RANGE", "monitor trampoline breakpoint rejected");
await Reject(() => monitor.AddBreakpointAsync(0), "MON51_BREAKPOINT_RANGE", "reset-vector breakpoint rejected");
await Reject(() => debug.ExecuteAsync(DebugAction.StepOut), "MON51_CAPABILITY", "step-out requires verified symbols and a caller");
await Reject(() => debug.ChangeWatchAsync("counter", false), "MON51_CAPABILITY", "source expressions do not reach a missing GDB adapter");
await Reject(() => debug.ToggleBreakpointAsync("src/main.c", 1), "MON51_CAPABILITY", "source breakpoint facade rejects address sessions explicitly");
await debug.ExecuteAsync(DebugAction.StepInto);
Check(monitor.Pc == 0x4d && fixture.Commands.Any(c => c.SequenceEqual(new byte[] { 8, 1 })), "native step 08 01 PC verified");
await monitor.SetPcAsync(0x4c);
await monitor.AddBreakpointAsync(0xa8);
Check(fixture.Code[0xa8] == 0xe5, "adding breakpoint does not modify CODE until continue");
await debug.ExecuteAsync(DebugAction.Continue);
await Task.Delay(350);
Check(debug.State == DebugState.Running && fixture.Code[0xa8] == 0xa5, "GO ACK and elapsed time never fabricate stop");
Check(Directory.GetFiles(Path.Combine(root, "user-data", "debug", "mon51-recovery")).Length == 1, "original byte journal persisted before patching");
fixture.SignalBreakpoint(0xa8);
using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
{
    while (debug.State == DebugState.Running)
    {
        await Task.Delay(20, deadline.Token);
    }
}
Check(debug.State == DebugState.Stopped && monitor.Pc == 0xa8 && fixture.Code[0xa8] == 0xe5, "asynchronous stop PC checked and original opcode restored");
await debug.ExecuteAsync(DebugAction.Continue);
Check(monitor.Pc == 0xa9 && fixture.Running, "continue from breakpoint executes original instruction before reinstalling");
await debug.ExecuteAsync(DebugAction.Pause);
Check(debug.State == DebugState.Stopped && fixture.Code[0xa8] == 0xe5, "manual pause restores patches and reads snapshot");
await debug.ExecuteAsync(DebugAction.Reset);
Check(monitor.Pc == 0xdbfd, "logical reset uses monitor trampoline, no erase");
fixture.CorruptNextData = true;
await Reject(() => monitor.ReadMemoryAsync(Mon51MemorySpace.Idata, 0, 4), "MON51_TIMEOUT", "bad data checksum rejected");
await debug.ExecuteAsync(DebugAction.Continue);
await debug.StopAsync();
Check(!debug.IsActive && debug.MonitorSession is null && fixture.Running && fixture.Code[0xa8] == 0xe5, "stop restores original opcode, resumes and clears facade");
using (var released = await hub.ReserveAsync(fixture.Key)) { checks.Add("connection released after stop"); }
Check(!fixture.Commands.Any(c => c[0] == 6), "no erase command exposed or transmitted");

var uncertain = new Mon51OfflineTransport();
await using (var session = new Mon51DebugSession(Path.Combine(root, "uncertain")))
{
    await session.ConnectAsync(hub, uncertain, project);
    await session.AddBreakpointAsync(0xa8);
    uncertain.FailNextBreakpointWrite = true;
    try
    {
        await session.ExecuteAsync(DebugAction.Continue);
        throw new InvalidOperationException("write failure accepted");
    }
    catch (IOException) { }
    Check(session.State == DebugState.Faulted && uncertain.Code[0xa8] == 0xa5 && Directory.GetFiles(Path.Combine(root, "uncertain", "debug", "mon51-recovery")).Length == 1, "uncertain write preserves original-byte recovery journal");
    await session.StopAsync();
    Check(uncertain.Code[0xa8] == 0xe5 && Directory.GetFiles(Path.Combine(root, "uncertain", "debug", "mon51-recovery")).Length == 0, "cleanup recovers uncertain write and removes only verified journal");
}
var restoration = new Mon51OfflineTransport();
var failedSession = new Mon51DebugSession(Path.Combine(root, "restore-failure"));
await failedSession.ConnectAsync(hub, restoration, project);
await failedSession.AddBreakpointAsync(0xa8);
await failedSession.ExecuteAsync(DebugAction.Continue);
restoration.FailRestore = true;
try { await failedSession.StopAsync(); throw new InvalidOperationException("restoration failure accepted"); } catch (IOException) { }
Check(failedSession.State == DebugState.Faulted && !failedSession.HasConnection && !restoration.Running && Directory.GetFiles(Path.Combine(root, "restore-failure", "debug", "mon51-recovery")).Length == 1, "failed restore closes port, preserves evidence and leaves target paused");
await Reject(() => failedSession.StopAsync(), "MON51_RECOVERY", "repeated stop cannot falsely confirm restoration");
var forbiddenReconnect = new Mon51DebugSession(Path.Combine(root, "restore-failure"));
await Reject(() => forbiddenReconnect.ConnectAsync(hub, new Mon51OfflineTransport(), project), "MON51_RECOVERY", "pending recovery record blocks silent reconnect");
await Reject(() => forbiddenReconnect.ConnectAsync(hub, new Mon51OfflineTransport(), Path.Combine(root, "different-project")), "MON51_RECOVERY", "switching project cannot bypass pending port recovery");
await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), $"PASS {checks.Count} Mon51 checks. Offline protocol fixture only; no serial or hardware access.\n" + string.Join('\n', checks));
await SourceChecks.RunAsync(root, checks);
await ComplexChecks.RunAsync(root, checks);
await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), $"PASS {checks.Count} Mon51 checks. Offline protocol fixture only; no serial or hardware access.\n" + string.Join('\n', checks));
Console.WriteLine($"PASS {checks.Count} Mon51 checks");
return 0;
