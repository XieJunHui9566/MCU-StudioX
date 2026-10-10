using StudioX.Application;
using StudioX.Application.StcDebugging;
using StudioX.Devices;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class SetupChecks
{
    public static async Task<int> RunAsync(string packFile, string runtime, string source, string output)
    {
        if (Directory.Exists(output)) {throw new InvalidOperationException("Use a new validation directory.");}
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) {throw new InvalidOperationException(name);} checks.Add(name); }
        async Task Reject(Func<Task> action, string code, string name)
        {
            try { await action(); throw new InvalidOperationException("Accepted: " + name); }
            catch (StudioXException ex) when (ex.Code == code) { checks.Add(name); }
        }
        var packs = new PackRepository(Path.Combine(output, "packs"));
        var pack = await packs.ImportAsync(packFile);
        var device = pack.Manifest.Devices.Single(d => d.Id == "IAP15F2K61S2");
        var descriptor = device.MonitorFirmware!;
        var packagedImage = PathBoundary.Resolve(pack.RootDirectory, descriptor.ImageFile);
        var firmware = await File.ReadAllBytesAsync(packagedImage);
        StcMonitorImage.ValidateSetup(firmware);
        Check(pack.Manifest.Version == "0.1.2" && descriptor.Version == "2.5.0" &&
            pack.Manifest.Devices.Count(d => d.MonitorFirmware is not null) == 1,
            "package carries the verified monitor only for the exact supported target");
        Check(!Directory.EnumerateFiles(pack.RootDirectory, "*.exe", SearchOption.AllDirectories).Any() &&
            !Directory.EnumerateFiles(pack.RootDirectory, "*.dll", SearchOption.AllDirectories).Any(),
            "monitor package contains firmware and provenance, without vendor executables or drivers");
        var project = Path.Combine(output, "project");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith(".build" + Path.DirectorySeparatorChar) || relative.StartsWith(".git" + Path.DirectorySeparatorChar)) {continue;}
            var destination = PathBoundary.Resolve(project, relative.Replace('\\', '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        await using var hub = new DeviceHub();
        var catalog = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
        var isp = new StcIspService(catalog, runtime, Path.Combine(output, "data"));
        var setup = new Mon51SetupService(isp, hub, packs, () => false);
        var projectFile = PathBoundary.Resolve(project, ".studiox/project.json");
        var projectBefore = await File.ReadAllBytesAsync(projectFile);
        var selected = (await setup.ListFirmwareSourcesAsync(project)).Single();
        Check(!selected.IsProjectPack, "old project discovers monitor in a separate compatible package without migrating compile locks");
        var prepared = await setup.PrepareAsync(project, "COM18", "IAP15F2K61S2", selected);
        Check((await File.ReadAllBytesAsync(projectFile)).SequenceEqual(projectBefore), "monitor preparation preserves the old project package and tool lock bytes");
        Check((await File.ReadAllBytesAsync(prepared.Image)).SequenceEqual(firmware) &&
            File.Exists(Path.Combine(Path.GetDirectoryName(prepared.Image)!, "monitor-source.json")),
            "package resource is snapshotted with its selected identity without a vendor EXE or import cache");
        await Reject(() => setup.PrepareAsync(project, "COM18", "IAP15F2K61S2", selected with { PackContentHash = "changed" }),
            "MON51_SETUP_PACK", "replaced selected package identity rejected before COM access");
        await Reject(() => setup.PrepareAsync(project, "COM18", "IAP15F2K61S2", selected with { ImageSha256 = "changed" }),
            "MON51_SETUP_FIRMWARE", "changed selected firmware identity rejected before preparation");
        var corrupt = firmware.ToArray();
        corrupt[100] ^= 1;
        await File.WriteAllBytesAsync(packagedImage, corrupt);
        await Reject(() => setup.PrepareAsync(project, "COM18", "IAP15F2K61S2", selected), "PACK_HASH", "tampered installed firmware rejected by full package verification");
        await Reject(() => Task.Run(() => PackValidator.Validate(pack.Manifest, pack.RootDirectory)), "PACK_MONITOR_HASH", "descriptor SHA rejects tampering even without the archive index");
        await File.WriteAllBytesAsync(packagedImage, firmware);
        foreach (var (bad, code) in new[]
        {
            (descriptor with { ImageFile = "../monitor.bin" }, "PATH_UNSAFE"),
            (descriptor with { ImageFile = "debug/mon51/missing.bin" }, "PACK_FILE_MISSING"),
            (descriptor with { ImageBytes = 61439 }, "PACK_MONITOR_HASH"),
            (descriptor with { ImageSha256 = "garbage" }, "PACK_MONITOR")
        })
        {
            var invalid = pack.Manifest with { Devices = pack.Manifest.Devices.Select(d => d.Id == device.Id ? d with { MonitorFirmware = bad } : d).ToArray() };
            await Reject(() => Task.Run(() => PackValidator.Validate(invalid, pack.RootDirectory)), code, "invalid monitor descriptor rejected: " + code + " / " + bad.ImageFile);
        }
        await isp.VerifyPreparedAsync(prepared);
        Check(prepared.MonitorSetup && prepared.HighestAddress == 0xefff && prepared.ImageSha256 == StcMonitorImage.SetupSha256,
            "native setup prepares dedicated encoding and preserves clock without saving ordinary ISP settings");
        var guardChecks = await new ProcessRunner().RunAsync(new(prepared.PythonExecutable,
            ["-B", Path.Combine(AppContext.BaseDirectory, "MonitorGuardChecks.py"), prepared.GuardScript, prepared.Image, Path.Combine(output, "guard-checks.json")],
            output, TimeSpan.FromSeconds(20), RemoveEnvironment: ["PYTHONHOME", "PYTHONPATH"]));
        Check(guardChecks.Success, "dedicated ISP packet adapter rejects unsupported targets and failed ACK without hardware: " + guardChecks.StandardOutput + guardChecks.StandardError);
        await Reject(() => isp.DownloadPreparedAsync(prepared), "STC_ISP_CHANNEL", "ordinary ISP entry refuses monitor setup preparation");
        await Reject(() => setup.PrepareAsync(project, "COM18", "IAP15L2K61S2", selected), "MON51_SETUP_TARGET", "unconfirmed or different exact target rejected offline");
        using (await hub.ReserveAsync("serial:COM18"))
            {await Reject(() => setup.ExecuteAsync(prepared, null), "DEVICE_OWNED", "setup cannot steal a reserved IDE serial connection");}
        await Reject(() => new Mon51SetupService(isp, hub, packs, () => true).ExecuteAsync(prepared, null),
            "MON51_SETUP_SESSION", "active debug session refuses monitor installation before device access");
        var altered = await File.ReadAllBytesAsync(prepared.Image);
        altered[100] ^= 1;
        await File.WriteAllBytesAsync(prepared.Image, altered);
        await Reject(() => isp.VerifyPreparedAsync(prepared with { ImageSha256 = StcMonitorImage.Hash(altered) }), "MON51_FIRMWARE",
            "rewritten hash cannot bypass fixed monitor identity");
        var builds = new BuildService(catalog);
        var workflow = new StcBuildWorkflowService(builds, () => false);
        var normal = new ProjectBuildSettings(Optimization: CompilerOptimization.O0, DebugInfo: CompilerDebugInfo.None, CodeRomSizeBytes: 60000);
        await builds.SaveSettingsAsync(project, normal);
        var debugSettings = await workflow.PrepareDebugAsync(project);
        Check(debugSettings.Mon51Profile && debugSettings.DebugInfo == CompilerDebugInfo.Standard && debugSettings.CodeRomSizeBytes == 0xdbfd,
            "debug workflow enables CDB and monitor memory bounds while preserving optimization");
        var debugBuild = await builds.BuildAsync(project);
        Check(debugBuild.Success, "real SDCC debug build succeeds: " + debugBuild.Summary);
        var artifact = await StcDebugArtifacts.ReadAsync(project);
        Check(artifact.Present.Skip(3).Any(present => present) && File.Exists(artifact.SymbolsPath), "latest debug build provides native image and source symbols");
        var clock = await isp.LoadSettingsAsync(project);
        var restored = await workflow.PrepareDownloadAsync(project);
        Check(restored == normal && !File.Exists(PathBoundary.Resolve(project, ".build/studiox-build-receipt.json")), "ordinary download restores compiler/capacity preferences and invalidates debug receipt");
        var normalBuild = await builds.BuildAsync(project);
        Check(normalBuild.Success && !((await builds.LoadSettingsAsync(project)).Mon51Profile), "real SDCC ordinary build succeeds without monitor profile");
        var preview = await isp.PreviewAsync(project, clock);
        Check(preview.ExpectedModel == "IAP15F2K61S2", "ordinary build goes through original ISP preview and never creates a debug session");
        var second = await workflow.PrepareDebugAsync(project);
        Check(second == debugSettings, "debug after ordinary download regenerates monitor profile consistently");
        var main = Path.Combine(project, "src", "main.c");
        await File.WriteAllTextAsync(main, (await File.ReadAllTextAsync(main)).Replace("++app_counter;", "app_counter += 3;", StringComparison.Ordinal));
        Check((await builds.BuildAsync(project)).Success && (await StcDebugArtifacts.ReadAsync(project)).ImageSha256 != artifact.ImageSha256,
            "next debug build consumes changed current source instead of reusing an earlier image");
        await Reject(() => new StcBuildWorkflowService(builds, () => true).PrepareDebugAsync(project), "STC_BUILD_SESSION", "active debugger prevents rebuild or channel changes");
        Check(!File.Exists(prepared.LogPath), "setup preparation never launches external programming");
        await File.WriteAllTextAsync(Path.Combine(output, "result.txt"), $"PASS {checks.Count} setup/workflow checks; no COM access or hardware writes.\n" + string.Join('\n', checks));
        Console.WriteLine($"PASS {checks.Count} setup/workflow checks");
        return 0;
    }
}
