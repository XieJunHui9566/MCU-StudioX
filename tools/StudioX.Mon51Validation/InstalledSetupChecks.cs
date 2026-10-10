using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

internal static class InstalledSetupChecks
{
    public static async Task<int> RunAsync(string runtime, string data, string project, string catalog, string output)
    {
        if (Directory.Exists(output)) {throw new InvalidOperationException("Use a new evidence directory.");}
        Directory.CreateDirectory(output);
        var manifestPath = PathBoundary.Resolve(project, ".studiox/project.json");
        var before = await File.ReadAllBytesAsync(manifestPath);
        await using var services = new WorkbenchService(runtime, data);
        var imported = await services.Packs.ImportBundledMissingAsync(catalog);
        if (imported.Failures.Count != 0) {throw new InvalidOperationException("Bundled package import failed: " + string.Join("\n", imported.Failures));}
        var sources = await services.Mon51Setup.ListFirmwareSourcesAsync(project);
        var selected = sources.Single(s => s.PackId == "stc.stc8" && s.PackVersion == "0.1.2");
        var prepared = await services.Mon51Setup.PrepareAsync(project, "COM18", "IAP15F2K61S2", selected);
        await services.StcIsp.VerifyPreparedAsync(prepared);
        if (!(await File.ReadAllBytesAsync(manifestPath)).SequenceEqual(before) || !prepared.MonitorSetup ||
            prepared.ImageSha256 != StcMonitorImage.SetupSha256 || File.Exists(prepared.LogPath) || services.Debugger.IsActive)
            {throw new InvalidOperationException("Installed package preparation altered locks, started a session, or used another payload.");}
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            Passed = true, Source = selected, prepared.ImageSha256, prepared.DataBytes, ProjectCompileLockUnchanged = true,
            VendorExecutableRequired = false, SerialAccess = false, HardwareWrites = false,
            ApplicationAssemblySha256 = StcMonitorImage.Hash(await File.ReadAllBytesAsync(typeof(WorkbenchService).Assembly.Location)),
            PackagesAssemblySha256 = StcMonitorImage.Hash(await File.ReadAllBytesAsync(typeof(StudioX.Packages.PackRepository).Assembly.Location))
        });
        Console.WriteLine("PASS installed package discovery and dedicated preparation; no COM access or hardware writes.");
        return 0;
    }
}
