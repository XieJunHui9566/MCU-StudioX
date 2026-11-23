using C51A.Core;
using C51A.Transport;
using StudioX.Application;
using StudioX.Application.StcDebugging;
using StudioX.Devices;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;
using StudioX.Packages;
using System.IO.Ports;

if (args.Length != 3)
{
    throw new ArgumentException("pack, runtime, isolated output required");
}
string output = Path.GetFullPath(args[2]);
Directory.CreateDirectory(output);
var checks = new List<string>();
void Check(bool value, string name)
{
    if (!value)
    {
        throw new Exception("FAIL " + name);
    }
    checks.Add(name);
    Console.WriteLine("PASS " + name);
}
var packs = new PackRepository(Path.Combine(output, "packs"));
var pack = await packs.ImportAsync(args[0]);
string project = Path.Combine(output, "示教联调工程");
if (!Directory.Exists(project))
{
    await new ProjectService().CreateAsync(pack, "STC89C52RC", "bare", "示教联调工程", project);
}
string source = Path.Combine(project, "src", "main.c");
await File.WriteAllTextAsync(source, """
__sfr __at (0x90) P1;
volatile __data unsigned char counter;
void increment(void)
{
    counter++;
    P1 = ~counter;
}
void main(void)
{
    counter = 0;
    while (1)
    {
        increment();
    }
}
""");
var builds = new BuildService(new ToolsetCatalog(Path.Combine(args[1], "toolsets")));
var selected = await new StcBuildWorkflowService(builds, () => false).PrepareTeachingDebugAsync(project);
Check(!selected.Mon51Profile && selected.DebugInfo == CompilerDebugInfo.Standard, "simulated debug keeps full chip resources and enables CDB");
var build = await builds.BuildAsync(project);
Check(build.Success, "real SDCC build: " + build.Summary);
var bundle = await StcDebugArtifacts.ReadTeachingAsync(project);
var symbols = Mon51Symbols.Parse(bundle.SymbolsText, project, bundle.SourceFiles);
var board = new Board();
object sync = new();
bool running = false;
bool loaded = false;
var target = new SimulatorDebugTarget(board, value => running = value, () => loaded = true);
using var lifetime = new CancellationTokenSource();
using var server = new SimulatorDebugServer(target, sync);
server.Diagnostic += Console.WriteLine;
server.Start();
var worker = Task.Run(async () =>
{
    while (!lifetime.IsCancellationRequested)
    {
        lock (sync)
        {
            if (running)
            {
                for (int i = 0; i < 300 && target.BeforeInstruction(); i++)
                {
                    board.Cpu.Step();
                }
            }
        }
        await Task.Delay(1);
    }
});
try
{
    await using var hub = new DeviceHub();
    await using var debug = new DebugSessionService(Path.Combine(output, "user-data"), hub);
    debug.Output += message => File.AppendAllText(Path.Combine(output, "debug.log"), message + "\n");
    await debug.OpenProjectAsync(project);
    await debug.StartTeachingSimulatorAsync();
    Check(loaded && !debug.IsHardware && debug.MonitorSession is { IsTeachingPlatform: true, HasSymbols: true, Pc: 0 }, "actual named pipe loads program and binds source at reset");
    Check(board.Cpu.Code.AsSpan(0, bundle.Code.Length).ToArray().Where((value, i) => bundle.Present[i] && value != bundle.Code[i]).Count() == 0, "program readback matches actual executing CPU");
    using (var other = new SimulatorMon51Endpoint(target, "other-debugger", true))

    {
        lock (sync)
        {
            Check(!other.Acquire(), "second debugger cannot own the same CPU");
        }
    }
    if (!debug.Breakpoints.Any())
    {
        await debug.ToggleBreakpointAsync("src/main.c", 5);
    }
    Check(debug.Breakpoints.Single().Verified, "source breakpoint is bound from real CDB");
    await debug.ExecuteAsync(DebugAction.Continue);
    async Task Stopped()
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (debug.State == DebugState.Running && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Check(debug.State == DebugState.Stopped, "asynchronous stop confirmed: " + debug.Reason);
    }
    await Stopped();
    var monitor = debug.MonitorSession!;
    Check(await monitor.EvaluateAsync("counter") == "0", "first breakpoint before increment");
    var initialTime = board.Microseconds;
    await Task.Delay(80);
    Check(board.Microseconds == initialTime, "paused CPU and board simulation time stay synchronized");
    await debug.ExecuteAsync(DebugAction.StepInto);
    await Stopped();
    Check(await monitor.EvaluateAsync("counter") == "1", "source step executes counter increment");
    await debug.ExecuteAsync(DebugAction.StepInto);
    await Stopped();
    Check(board.Cpu.PortLatch(1) == 0xfe, "source step updates the same board P1 pins");
    await debug.ExecuteAsync(DebugAction.Continue);
    await Stopped();
    Check(await monitor.EvaluateAsync("counter") == "1", "continued loop hits breakpoint again");
    await debug.ExecuteAsync(DebugAction.Reset);
    Check(monitor.Pc == 0, "virtual reset uses real vector zero without IAP trampoline");
    await debug.StopAsync();
    for (int i = 0; i < 50 && target.Owner is not null; i++)
    {
        await Task.Delay(10);
    }
    Check(target.Owner is null && !running && !board.Cpu.Code.Take(8192).Contains((byte)0xa5), "disconnect releases ownership and restores breakpoint bytes");
    await debug.StartTeachingSimulatorAsync();
    if (!debug.Breakpoints.Any())
    {
        await debug.ToggleBreakpointAsync("src/main.c", 5);
    }
    await debug.ExecuteAsync(DebugAction.Continue);
    await Stopped();
    server.DisconnectClients();
    for (int i = 0; i < 100 && target.Owner is not null; i++)
    {
        await Task.Delay(10);
    }
    Check(target.Owner is null && !running, "platform end releases named-pipe owner after debugger interruption");
    await debug.StopAsync();
    await debug.StartTeachingSimulatorAsync();
    Check(debug.State == DebugState.Stopped, "reconnect after interrupted simulator session is not blocked by physical recovery records");
    await debug.StopAsync();
    for (int i = 0; i < 100 && target.Owner is not null; i++)
    {
        await Task.Delay(10);
    }
    // 单独注册的虚拟 COM，永远不枚举或连接实物板。
    server.OpenKeil();
    var ports = VirtualPortProfile.LoadDebug();
    var ordinary = VirtualPortProfile.Load();
    Check(ports.HostPort != ordinary.HostPort && ports.SimulatorPort != ordinary.SimulatorPort, "Keil has an independent virtual pair");
    using var host = new SerialPort(ports.HostPort, 115200) { ReadTimeout = 2500, WriteTimeout = 1000 };
    host.Open();
    host.Write(new byte[] { 0x80 }, 0, 1);
    int syncFirst = host.ReadByte();
    host.Write(new byte[] { 0x80 }, 0, 1);
    int syncSecond = host.ReadByte();
    Check((syncFirst == 0 && syncSecond == 255) || (syncFirst == 255 && syncSecond == 0), "original Keil driver alternating synchronization bytes");
    byte[] Exchange(byte[] payload, int count)
    {
        var frame = Mon51Client.Encode(payload);
        host.Write(frame, 0, frame.Length);
        var result = new byte[count];
        int received = 0;
        while (received < count)
        {
            received += host.Read(result, received, count - received);
        }
        return result;
    }
    Check(Exchange([0, 0], 7).SequenceEqual(new byte[] { 6, 0, 0x56, 0x32, 0x2e, 0x35, 4 }), "MON51 V2.5 handshake across actual debug COM pair");
    Check(Exchange([4, 6, 0, 0x13, 6], 8).SequenceEqual(new byte[] { 2, 0, 0, 0, 0, 0, 0, 254 }), "Keil private monitor configuration has separate storage");
    Exchange([2, 4, 0, 0x90, 1, 0xa6], 7);
    Exchange([2, 1, 0, 0x90, 1, 0x39], 7);
    Check(board.Cpu.PortLatch(1) == 0xa6 && board.Cpu.Ram[0x90] == 0x39, "Keil direct SFR and upper IDATA remain distinct");
    Exchange([2, 0, 0, 0, 0], 7);
    var registers = Exchange([0, 1], 21);
    Check(registers[13] == 0xdb && registers[14] == 0xfd, "legacy driver sees virtual reset alias while CPU remains STC89");
    var alias = Exchange([4, 5, 0xdb, 0xfd, 4], 6);
    Check(alias.Skip(1).Take(3).SequenceEqual(board.Cpu.Code.Take(3)) && alias[4] == 0xff, "Keil four-byte reset-vector read crosses legacy alias boundary safely");
    var step = Exchange([8, 1], 7);
    Check((step[2] << 8 | step[3]) == board.Cpu.PC && board.Cpu.Instructions > 0, "legacy instruction step executes the actual CPU");
    var pcRead = Exchange([4, 0, 0, 0, 2], 4);
    Check((pcRead[1] << 8 | pcRead[2]) == board.Cpu.PC, "Keil stop PC query matches executing CPU");
    lock (sync)
    {
        board.Cpu.Code[0] = 0x80;
        board.Cpu.Code[1] = 0xfe;
    }
    Exchange([2, 0, 0, 0, 0], 7);
    Exchange([8, 0], 7);
    host.Write(new byte[] { 0xff }, 0, 1);
    Check(host.ReadByte() == 0 && !running, "Keil raw pause byte stops running CPU and returns stop notification");
    host.Close();
    server.DisconnectClients();
    await Task.Delay(200);
    Check(target.Owner is null, "manual end releases legacy debugger session");
    // 原子下载在提交前断开时保留旧固件。
    var original = board.Cpu.Code.ToArray();
    using (var endpoint = new SimulatorMon51Endpoint(target, "cancelled-download", false))
    {
        lock (sync)
        {
            endpoint.Acquire();
            endpoint.Feed(Mon51Client.Encode([6, 0, 0, 0, 0]));
            endpoint.Feed(Mon51Client.Encode([2, 5, 0, 3, 1, 0x22]));
        }
    }
    Check(original.SequenceEqual(board.Cpu.Code), "incomplete debug download retains previous firmware");
    await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
    {
        Success = true,
        Hardware = false,
        Project = project,
        KeilPort = ports.HostPort,
        Checks = checks
    });
    Console.WriteLine($"PASS {checks.Count} integrated simulator debug checks");
}
finally
{
    lifetime.Cancel();
    await worker;
}
