using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class WchDownloadChecks
{
    public static async Task RunAsync(string runtime, string archive, string output)
    {
        var root = Path.GetFullPath(output);
        if (Directory.Exists(root)) throw new ArgumentException("Use a new output directory");
        var pack = await new PackRepository(Path.Combine(root, "packs")).ImportAsync(Path.GetFullPath(archive));
        var catalog = new ToolsetCatalog(Path.Combine(Path.GetFullPath(runtime), "toolsets"));
        var downloads = new OpenOcdService(catalog);
        var builds = new BuildService(catalog);
        foreach (var device in pack.Manifest.Devices)
        {
            var project = Path.Combine(root, device.Id);
            await new ProjectService().CreateAsync(pack, device.Id, "spl", device.Id, project);
            var report = await builds.BuildAsync(project);
            if (!report.Success) throw new Exception(report.Log);
            var config = (await downloads.ConfigurationAsync(project))!;
            var plan = await downloads.PrepareAsync(project, config.Options);
            if (!plan.Arguments.Contains(Path.Combine(project, "device", "interface", "wch-link.cfg")) ||
                !plan.Arguments.Contains("transport select sdi")) throw new Exception("Missing pack interface or SDI transport");
            if (!File.ReadAllText(Path.Combine(project, "device/interface/wch-link.cfg")).Contains("\npage_erase")) throw new Exception("Page mode required");
            var operation = plan.Arguments[^1];
            if (operation.IndexOf("studiox_check_target", StringComparison.Ordinal) > operation.IndexOf("flash write_image", StringComparison.Ordinal)) throw new Exception("Guard must precede writes");
            var chip = device.Id switch { "CH32V307VCT6" => "0x30700568", "CH32V307RCT6" => "0x30710568", _ => "0x30730568" };
            // No init: exercise the actual Tcl identity/memory/protection guards using fake read-only registers.
            var script = $"set fake_chip {chip}; set fake_user 0x40bf; set fake_rdp 0x5aa5; " +
                "proc read_memory {address width count} {global fake_chip fake_user fake_rdp; if {$address == 0x1ffff704} {return [list $fake_chip]}; if {$address == 0x1ffff802} {return [list $fake_user]}; return [list $fake_rdp]}; " +
                "studiox_check_target; set fake_chip 0; if {![catch {studiox_check_target}]} {error CHIP_GUARD}; " +
                $"set fake_chip {chip}; set fake_user 0x00ff; if {{![catch {{studiox_check_target}}]}} {{error SPLIT_GUARD}}; " +
                "set fake_user 0x41bf; if {![catch {studiox_check_target}]} {error COMPLEMENT_GUARD}; " +
                "set fake_user 0x40bf; set fake_rdp 0; if {![catch {studiox_check_target}]} {error PROTECTION_GUARD}; echo STUDIOX_OFFLINE_OK; shutdown";
            var result = await new ProcessRunner().RunAsync(new(plan.Tools.Tool("openocd"), plan.Arguments[..^2].Concat(["-c", script]).ToArray(), project,
                TimeSpan.FromSeconds(15), ToolsetEnvironment.Create(plan.Tools), RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
            await File.WriteAllTextAsync(Path.Combine(project, "offline.log"), result.StandardOutput + result.StandardError);
            if (!result.Success || !(result.StandardOutput + result.StandardError).Contains("STUDIOX_OFFLINE_OK")) throw new Exception(result.StandardOutput + result.StandardError);
            foreach (var invalid in new[] { new DownloadOptions("wch-link", 1500), new DownloadOptions("wch-link", 6000, "unsupported-serial") })
            {
                try { await downloads.SaveOptionsAsync(project, invalid); throw new Exception("Expected rejection"); }
                catch (StudioXException ex) when (ex.Code == "DOWNLOAD_OPTIONS") { }
            }
            Console.WriteLine($"PASS {device.Id}: build, download preparation, SDI/page erase, chip/split/protection guard, settings; no hardware access");
        }
    }
}
