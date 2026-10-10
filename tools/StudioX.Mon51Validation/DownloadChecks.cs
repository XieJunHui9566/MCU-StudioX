using StudioX.Application;
using StudioX.Application.StcDebugging;
using StudioX.Devices;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

internal static class DownloadChecks
{
    public static async Task<int> RunAsync(string sourceProject, string output)
    {
        if (Directory.Exists(output)) { throw new InvalidOperationException("Use a new validation directory."); }
        var project = Path.Combine(output, "project");
        Directory.CreateDirectory(project);
        foreach (var path in Directory.EnumerateFiles(sourceProject, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceProject, path);
            if (relative.StartsWith(".build" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { continue; }
            var target = PathBoundary.Resolve(project, relative.Replace('\\', '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target);
        }
        Directory.CreateDirectory(Path.Combine(project, ".build"));
        foreach (var file in new[] { "studiox-build-receipt.json", "firmware.hex", "firmware.cdb" })
        {
            File.Copy(Path.Combine(sourceProject, ".build", file), Path.Combine(project, ".build", file));
        }
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            if (!value) { throw new InvalidOperationException(name); }
            checks.Add(name);
        }
        async Task Reject(Func<Task> action, string code, string name)
        {
            try { await action(); throw new InvalidOperationException("Accepted: " + name); }
            catch (StudioXException error) when (error.Code == code) { checks.Add(name); }
        }
        await using var hub = new DeviceHub();
        var fixture = new Mon51OfflineTransport { AllowUserProgramDownload = true, UseMemoryImage = true, UserEraseReplyDelay = TimeSpan.FromSeconds(2.5) };
        await using var debug = new DebugSessionService(Path.Combine(output, "user-data"), hub, _ => fixture);
        await debug.OpenProjectAsync(project);
        await debug.ToggleBreakpointAsync("src/main.c", 10);
        var selected = await debug.PrepareMon51DownloadAsync();
        Check(selected.UserBytes > 3 && selected.BinarySha256.Length == 64, "prepare selected real SDCC HEX/CDB without opening transport");
        await Reject(() => debug.DownloadAndStartMon51Async("COM18", 115200, "IAP15F2K61S2", selected with { UserBytes = selected.UserBytes + 1 }),
            "MON51_ARTIFACT_CHANGED", "changed selection rejected before connection and erase");
        Check(fixture.Commands.Count == 0, "no commands sent by rejected download selection");
        await debug.DownloadAndStartMon51Async("COM18", 115200, "IAP15F2K61S2", selected);
        checks.Add("user erase accepts delayed ACK beyond ordinary two-second response timeout");
        Check(debug.State == DebugState.Stopped && debug.MonitorSession!.Pc == 0xdbfd && debug.MonitorSession.HasSymbols,
            "download verifies full image and reset trampoline, stops at reset and automatically activates matching CDB");
        Check(debug.Breakpoints.Single() is { Verified: true, BoundLocation.Line: 10 }, "saved line-10 breakpoint binds only after downloaded image is verified");
        var writes = fixture.Commands.Where(command => command[0] == 2 && command[1] == 5).ToArray();
        Check(writes.Length > 1 && writes.Last()[2..5].SequenceEqual(new byte[] { 0, 0, 3 }) &&
            writes.Take(writes.Length - 1).All(command => (command[2] << 8 | command[3]) >= 3 &&
                (command[2] << 8 | command[3]) + command[4] <= 0xdbfd), "body writes avoid monitor and reset vector is committed separately last");
        Check(fixture.Commands.Count(command => command.SequenceEqual(new byte[] { 6, 0, 0, 0, 0 })) == 1 &&
            !fixture.Commands.Any(command => command[1] == 6), "one explicit Mon51 user erase and no configuration-space commands");
        Check(!fixture.Running && !fixture.Commands.Any(command => command.SequenceEqual(new byte[] { 8, 0 })), "successful download does not run before user continues");
        var bundle = await StcDebugArtifacts.ReadAsync(project);
        var expected = Enumerable.Range(0, selected.UserBytes).Select(i => bundle.Present[i] ? bundle.Code[i] : (byte)0xff).ToArray();
        Check(fixture.Code.Take(selected.UserBytes).SequenceEqual(expected), "full written binary includes expected erased gaps");
        await debug.StopAsync();

        var failed = new Mon51OfflineTransport { AllowUserProgramDownload = true, FailNextProgramWrite = true };
        var repaired = new Mon51OfflineTransport { AllowUserProgramDownload = true, UseMemoryImage = true };
        var attempts = 0;
        var recoveryData = Path.Combine(output, "recovery-data");
        await using var recovery = new DebugSessionService(recoveryData, hub, _ => attempts++ == 0 ? failed : repaired);
        await recovery.OpenProjectAsync(project);
        try
        {
            await recovery.DownloadAndStartMon51Async("COM18", 115200, "IAP15F2K61S2", selected);
            throw new InvalidOperationException("Injected write failure accepted");
        }
        catch (IOException) { }
        Check(!recovery.IsActive && recovery.MonitorSession is null && !failed.Running &&
            !failed.Commands.Any(command => command.SequenceEqual(new byte[] { 8, 0 })), "uncertain program write releases ownership without resuming incomplete firmware");
        Check(Directory.GetFiles(Path.Combine(recoveryData, "debug", "mon51-download-recovery")).Length == 1,
            "incomplete download record survives failed session cleanup");
        await Reject(() => recovery.StartMon51Async("COM18", 115200, "IAP15F2K61S2"), "MON51_DOWNLOAD_RECOVERY",
            "ordinary attach cannot bypass incomplete download recovery");
        await recovery.DownloadAndStartMon51Async("COM18", 115200, "IAP15F2K61S2", selected);
        Check(recovery.MonitorSession!.HasSymbols && Directory.GetFiles(Path.Combine(recoveryData, "debug", "mon51-download-recovery")).Length == 0,
            "explicit complete redownload recovers target and removes record only after verification");
        await recovery.StopAsync();
        await File.WriteAllTextAsync(Path.Combine(output, "result.txt"), $"PASS {checks.Count} Mon51 download checks on real SDCC build; offline fixture, no hardware access.\n" + string.Join('\n', checks));
        Console.WriteLine($"PASS {checks.Count} Mon51 download checks");
        return 0;
    }
}
