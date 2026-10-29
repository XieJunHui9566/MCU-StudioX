using System.Text.Json;
using StudioX.Application;
using StudioX.Application.StcDebugging;
using StudioX.Devices;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;
using StudioX.Packages;

internal static class LowVoltageChecks
{
    internal static async Task<int> RunAsync(string archive, string runtime, string vendorExecutable, string output)
    {
        if (Directory.Exists(output))
        {
            throw new InvalidOperationException("Use a new validation directory.");
        }
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            if (!value)
            {
                throw new InvalidOperationException(name);
            }
            checks.Add(name);
        }
        async Task Reject(Func<Task> action, string code, string name)
        {
            try
            {
                await action();
                throw new InvalidOperationException("Accepted: " + name);
            }
            catch (StudioXException ex) when (ex.Code == code) { checks.Add(name); }
        }
        const string target = StcMon51Targets.LowVoltageDevice;
        var packs = new PackRepository(Path.Combine(output, "packs"));
        var pack = await packs.ImportAsync(archive);
        var device = pack.Manifest.Devices.Single(d => d.Id == target);
        Check(device.FlashBytes == 62464 && device.RamBytes == 2048 && device.Architecture == "mcs51", "exact L model has 61 KiB Flash and 2 KiB SRAM");
        var firmware = await File.ReadAllBytesAsync(PathBoundary.Resolve(pack.RootDirectory, device.MonitorFirmware!.ImageFile));
        StcMonitorImage.ValidateSetup(firmware, target);
        var extracted = await AiCubeMonitorImporter.ReadSetupImageAsync(vendorExecutable, target);
        Check(extracted.SequenceEqual(firmware), "independent C# vendor resource extraction matches exact L image hash");
        await Reject(() => Task.Run(() => StcMonitorImage.ValidateSetup(firmware)), "MON51_FIRMWARE", "L monitor cannot be used as F monitor");
        var highVoltage = await AiCubeMonitorImporter.ReadSetupImageAsync(vendorExecutable);
        await Reject(() => Task.Run(() => StcMonitorImage.ValidateSetup(highVoltage, target)), "MON51_FIRMWARE", "F monitor cannot be used as L monitor");
        var project = Path.Combine(output, "project");
        await new ProjectService().CreateAsync(pack, target, "bare", "LowVoltageProbe", project);
        var catalog = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
        var builds = new BuildService(catalog);
        var workflow = new StcBuildWorkflowService(builds, () => false);
        var settings = await workflow.PrepareDebugAsync(project);
        Check(settings.Mon51Profile && settings.DebugInfo == CompilerDebugInfo.Standard && settings.CodeRomSizeBytes == 0xdbfd,
            "L debug workflow reserves monitor Flash/XDATA and enables native CDB");
        var build = await builds.BuildAsync(project);
        Check(build.Success, "real SDCC L debug build succeeds: " + build.Summary);
        var artifact = await StcDebugArtifacts.ReadAsync(project);
        Check(artifact.Present.Take(3).All(value => value) && File.Exists(artifact.SymbolsPath), "L build yields current code and CDB receipt");
        var data = Path.Combine(output, "data");
        await using var hub = new DeviceHub();
        var isp = new StcIspService(catalog, runtime, data);
        var setup = new Mon51SetupService(isp, hub, packs, () => false);
        var sources = await setup.ListFirmwareSourcesAsync(project);
        Check(sources.Count == 1 && sources[0].DeviceId == target, "L project discovers only exact L monitor resource");
        var prepared = await setup.PrepareAsync(project, "COM18", target, sources[0]);
        await isp.VerifyPreparedAsync(prepared);
        Check(prepared.MonitorSetup && prepared.ExpectedModel == target && prepared.ImageSha256 == StcMon51Targets.LowVoltageSetupSha256 && !File.Exists(prepared.LogPath),
            "native L setup is prepared and verified without opening a serial port");
        await Reject(() => setup.PrepareAsync(project, "COM18", StcMon51Targets.HighVoltageDevice, sources[0]), "MON51_SETUP_TARGET", "setup rejects F confirmation for L project before transport");
        await Reject(() => setup.PrepareAsync(project, "COM18", target, sources[0] with { ImageSha256 = StcMonitorImage.SetupSha256 }),
            "MON51_SETUP_FIRMWARE", "L source selection cannot substitute F firmware hash");
        await Reject(() => isp.PrepareMonitorAsync(project, "COM18", highVoltage), "MON51_FIRMWARE", "native L preparation refuses F bytes before programming");
        var fixture = new Mon51OfflineTransport { UseMemoryImage = true, AllowUserProgramDownload = true };
        var createdTransports = 0;
        await using var debug = new DebugSessionService(data, hub, _ => { createdTransports++; return fixture; });
        await debug.OpenProjectAsync(project);
        await Reject(() => debug.StartMon51Async("COM18", 115200, StcMon51Targets.HighVoltageDevice), "MON51_TARGET", "debug rejects mismatched project/model before creating transport");
        Check(createdTransports == 0, "wrong model never invokes transport factory");
        var download = await debug.PrepareMon51DownloadAsync();
        await debug.DownloadAndStartMon51Async("COM18", 115200, target, download);
        var session = debug.MonitorSession!;
        Check(session.TargetId == target && session.HasSymbols && debug.State == DebugState.Stopped && !debug.IsHardware,
            "L user download verifies all bytes and loads CDB using offline transport");
        await session.StepInstructionAsync();
        Check(session.State == DebugState.Stopped, "L instruction step uses shared Mon51 backend");
        await session.WriteMemoryAsync(Mon51MemorySpace.Xdata, 0x20, [0x5a]);
        Check((await session.ReadMemoryAsync(Mon51MemorySpace.Xdata, 0x20, 1))[0] == 0x5a, "L XDATA write is read back");
        await Reject(() => session.ReadMemoryAsync(Mon51MemorySpace.Xdata, 0x400, 1), "MON51_RANGE", "L monitor-reserved XDATA remains protected");
        await session.AddBreakpointAsync(0x100);
        await session.ExecuteAsync(DebugAction.Continue);
        var journals = Directory.GetFiles(Path.Combine(data, "debug/mon51-recovery"), "*.json");
        Check(journals.Length == 1 && JsonDocument.Parse(await File.ReadAllTextAsync(journals[0])).RootElement.GetProperty("target").GetString() == target,
            "L breakpoint recovery records the actual L identity");
        await using (var other = new Mon51DebugSession(data))
        {
            await Reject(() => other.ConnectAsync(hub, new Mon51OfflineTransport(), project), "MON51_RECOVERY", "switching F/L cannot bypass pending recovery on same port");
        }
        await session.ExecuteAsync(DebugAction.Pause);
        await debug.StopAsync();
        Check(Directory.GetFiles(Path.Combine(data, "debug/mon51-recovery"), "*.json").Length == 0, "L stop restores original breakpoint bytes and clears verified recovery");
        var ordinary = await workflow.PrepareDownloadAsync(project);
        Check(!ordinary.Mon51Profile, "L ordinary ISP workflow restores non-monitor build settings");
        await File.WriteAllTextAsync(Path.Combine(output, "result.txt"), $"PASS {checks.Count} IAP15L2K61S2 checks. Real SDCC compile and offline protocol only; no hardware accessed.\n" + string.Join('\n', checks));
        Console.WriteLine($"PASS {checks.Count} IAP15L2K61S2 checks");
        return 0;
    }
}
