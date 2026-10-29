using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Application;
using StudioX.Application.StcDebugging;
using StudioX.Devices;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>本次用户明确允许覆盖的 COM15 / IAP15L2K61S2 验收；准备、制作与用户程序验证分别调用。</summary>
internal static class LowVoltageHardwareChecks
{
    private const string Target = StcMon51Targets.LowVoltageDevice;
    private sealed record Plan(string Runtime, string Project, StcIspPreparation Monitor, Mon51DownloadPreparation Program);

    internal static async Task<int> PrepareAsync(string archive, string runtime, string output)
    {
        RequireIdentification(output);
        var repository = new PackRepository(Path.Combine(output, "packs"));
        var pack = await repository.ImportAsync(archive);
        var project = Path.Combine(output, "test-project");
        await new ProjectService().CreateAsync(pack, Target, "bare", "IAP15L_Mon51_Test", project);
        var source = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "mon51-fixtures/probe.c"));
        await File.WriteAllTextAsync(Path.Combine(project, "src/main.c"), source);
        var tools = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
        var builds = new BuildService(tools);
        await new StcBuildWorkflowService(builds, () => false).PrepareDebugAsync(project);
        var result = await builds.BuildAsync(project);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Summary);
        }
        await using var devices = new DeviceHub();
        var setup = new Mon51SetupService(new(tools, runtime, Path.Combine(output, "data")), devices, repository, () => false);
        var firmware = (await setup.ListFirmwareSourcesAsync(project)).Single();
        var monitor = await setup.PrepareAsync(project, "COM15", Target, firmware);
        await using var debug = new DebugSessionService(Path.Combine(output, "data"), devices);
        await debug.OpenProjectAsync(project);
        var program = await debug.PrepareMon51DownloadAsync();
        await JsonStore.WriteAsync(Path.Combine(output, "hardware-plan.json"), new Plan(runtime, project, monitor, program));
        Console.WriteLine($"Prepared {Target} COM15: monitor={monitor.ImageSha256}; user bytes={program.UserBytes}; user BIN SHA256={program.BinarySha256}");
        return 0;
    }

    internal static async Task<int> SetupAsync(string output)
    {
        RequireIdentification(output);
        var plan = await JsonStore.ReadAsync<Plan>(Path.Combine(output, "hardware-plan.json"));
        if (plan.Monitor.ExpectedModel != Target || plan.Monitor.Port != "COM15")
        {
            throw new InvalidOperationException("Hardware scope changed.");
        }
        var isp = new StcIspService(new(Path.Combine(plan.Runtime, "toolsets")), plan.Runtime, Path.Combine(output, "data"));
        Console.WriteLine("Dedicated L monitor setup is waiting on COM15; power/download switch is required.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var report = await isp.SetupMonitorAsync(plan.Monitor, new Progress<string>(Console.WriteLine), deadline.Token);
        await JsonStore.WriteAsync(Path.Combine(output, "monitor-setup-report.json"), report);
        if (!report.Success || !report.Log.Contains("STUDIOX_MONITOR_SETUP_COMPLETE POWER_CYCLE_REQUIRED", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(report.Log);
        }
        Console.WriteLine("L monitor write acknowledged. MCU power cycle is required before protocol verification.");
        return 0;
    }

    internal static async Task<int> VerifyAsync(string output)
    {
        RequireIdentification(output);
        var plan = await JsonStore.ReadAsync<Plan>(Path.Combine(output, "hardware-plan.json"));
        var checks = new List<string>();
        void Check(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
            Console.WriteLine("PASS " + message);
        }
        await using var devices = new DeviceHub();
        await using var debug = new DebugSessionService(Path.Combine(output, "data"), devices);
        debug.Output += Console.WriteLine;
        await debug.OpenProjectAsync(plan.Project);
        var selected = await debug.PrepareMon51DownloadAsync();
        if (selected != plan.Program)
        {
            throw new InvalidOperationException("Selected hardware test firmware changed.");
        }
        Console.WriteLine($"Downloading authorized {selected.UserBytes}-byte test through Mon51; SHA256={selected.BinarySha256}");
        await debug.DownloadAndStartMon51Async("COM15", 115200, Target, selected);
        var session = debug.MonitorSession!;
        Check(debug.IsHardware && session.Version == "V2.5" && session.TargetId == Target, "native L monitor handshake and hardware session identity");
        Check(session.HasSymbols && debug.State == DebugState.Stopped, "authorized test downloaded, fully read back and CDB loaded while paused");
        async Task WaitStopped()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (session.State == DebugState.Running)
            {
                await Task.Delay(25, deadline.Token);
            }
            Check(session.State == DebugState.Stopped, "execution stops with acknowledged native state");
        }
        async Task<string> ImageHash(Mon51DebugSession monitor)
        {
            var bytes = new List<byte>();
            for (var address = 0; address < selected.UserBytes; address += 128)
            {
                bytes.AddRange(await monitor.ReadMemoryAsync(Mon51MemorySpace.Code, (ushort)address, Math.Min(128, selected.UserBytes - address)));
            }
            return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
        }
        Check(await ImageHash(session) == selected.BinarySha256, "live user CODE SHA256 matches chosen test binary");
        await session.ConfigureSourceBreakpointAsync(new("checkpoint", "src/main.c", 7));
        await session.ExecuteAsync(DebugAction.Continue);
        await WaitStopped();
        Check(await session.EvaluateAsync("counter") == "0" && await session.EvaluateAsync("total") == "0", "first source breakpoint observes initialized DATA and XDATA counters");
        Check(session.Snapshot.Frames.Length >= 2 && session.Snapshot.Registers.Count() == 19, "real 8051 registers and verified checkpoint/main stack");
        await session.ExecuteAsync(DebugAction.StepInto);
        await WaitStopped();
        Check(await session.EvaluateAsync("counter") == "1" && await session.EvaluateAsync("total") == "0", "source step increments DATA counter exactly once");
        await session.ExecuteAsync(DebugAction.StepOut);
        await WaitStopped();
        Check(await session.EvaluateAsync("total") == "1", "step-out completes XDATA update and returns to caller");
        await session.ConfigureSourceBreakpointAsync(new("checkpoint", "src/main.c", 7, Condition: "counter >= 3"));
        await session.ExecuteAsync(DebugAction.Continue);
        await WaitStopped();
        Check(await session.EvaluateAsync("counter") == "3" && await session.EvaluateAsync("total") == "6", "conditional source breakpoint observes actual repeated execution");
        var original = await session.ReadMemoryAsync(Mon51MemorySpace.Xdata, 0x30, 1);
        await session.WriteMemoryAsync(Mon51MemorySpace.Xdata, 0x30, [(byte)(original[0] ^ 1)]);
        await session.WriteMemoryAsync(Mon51MemorySpace.Xdata, 0x30, original);
        Check((await session.ReadMemoryAsync(Mon51MemorySpace.Xdata, 0x30, 1)).SequenceEqual(original), "user scratch XDATA edit is read back and restored");
        await session.ChangeSourceBreakpointAsync("checkpoint", null);
        Check(await ImageHash(session) == selected.BinarySha256, "all breakpoint original bytes restored to exact test binary");
        var firstLog = session.LogPath;
        await debug.StopAsync();
        await using var reconnected = new Mon51DebugSession(Path.Combine(output, "data"), Target);
        await reconnected.ConnectAsync(devices, new SerialTransport(new("COM15", 115200)), plan.Project);
        Check(reconnected.Version == "V2.5" && await ImageHash(reconnected) == selected.BinarySha256, "warm reconnect reads unchanged test firmware after normal exit");
        await reconnected.StopAsync();
        Check(!reconnected.HasConnection, "normal exit releases COM15 and resumes test program");
        await JsonStore.WriteAsync(Path.Combine(output, "hardware-result.json"), new
        {
            target = Target,
            port = "COM15",
            hardware = true,
            success = true,
            checks,
            userBytes = selected.UserBytes,
            userBinarySha256 = selected.BinarySha256,
            monitorSha256 = plan.Monitor.ImageSha256,
            firstLog,
            reconnectLog = reconnected.LogPath,
            completedUtc = DateTimeOffset.UtcNow
        });
        Console.WriteLine($"PASS {checks.Count} real IAP15L2K61S2 checks");
        return 0;
    }

    private static void RequireIdentification(string output)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "isp-identification.json")));
        var identity = document.RootElement;
        if (identity.GetProperty("model").GetString() != Target || identity.GetProperty("port").GetString() != "COM15" ||
            identity.GetProperty("bootloader").GetString() != "7.2.5S" || !identity.GetProperty("status_hex").GetString()!.EndsWith("70", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("This invocation requires this session's exact COM15 IAP15L2K61S2 identification.");
        }
    }
}
