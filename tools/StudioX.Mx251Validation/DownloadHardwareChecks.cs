using StudioX.Application;
using StudioX.Devices;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>仅由显式硬件验收命令调用；外层必须持有实验租约，并在 finally 恢复约定固件。</summary>
internal static class DownloadHardwareChecks
{
    internal static async Task RunAsync(string archive, string toolsRoot, string output)
    {
        const string expectedDevice = @"\\?\hid#vid_34bf&pid_ff0f&mi_00#9&fd3041a&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        if (Directory.Exists(output))
        {
            throw new IOException("New hardware evidence directory required");
        }
        Directory.CreateDirectory(output);
        var catalog = new ToolsetCatalog(toolsRoot);
        var builder = new BuildService(catalog);
        var isp = new Mx251IspService(catalog, Path.Combine(output, "data"));
        await using var devices = new DeviceHub();
        var service = new Mx251DownloadService(isp, builder, devices);
        var bridges = await service.ListDevicesAsync();
        if (bridges.Count != 1 || bridges[0].Path != expectedDevice)
        {
            throw new IOException("Existing hardware identity whitelist mismatch");
        }
        var pack = await new PackRepository(Path.Combine(output, "packs")).ImportAsync(archive);
        var root = Path.Combine(output, "math-project");
        await new ProjectService().CreateAsync(pack, "AI8051U-34K64", "registers", "math-download-validation", root);
        var tools = await catalog.ResolveAsync(Mx251Profile.ToolsetId, Mx251Profile.Version, Mx251Profile.CompilerId);
        var sample = Path.Combine(tools.RootDirectory, "examples/fixed/full-math");
        foreach (var file in Directory.EnumerateFiles(sample, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(root, Path.GetRelativePath(sample, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        File.Copy(Path.Combine(sample, "project.json"), Path.Combine(root, "mx251.json"), true);
        var build = await builder.BuildAsync(root);
        await File.WriteAllTextAsync(Path.Combine(output, "build.log"), build.Log);
        if (!build.Success)
        {
            throw new Exception("Math build failed");
        }
        await service.SaveSettingsAsync(root, new(expectedDevice));
        var prepared = await service.PrepareAsync(root);
        if (!prepared.ImageSha256.Equals("fff7b2b3bbc5068fd1b57e29add4254add8d6a64f94d6eb58179464bb136b40e", StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception("Math firmware differs from frozen qualification; hardware not opened");
        }
        await JsonStore.WriteAsync(Path.Combine(output, "prepared.json"), prepared);
        Console.WriteLine("VERIFIED_FROZEN_MATH_IMAGE " + prepared.ImageSha256);
        var report = await service.DownloadPreparedAsync(prepared, new Progress<string>(Console.WriteLine));
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), report);
        if (!report.Success || !report.Result.ConfigurationReadback || !report.Result.ProgramAcknowledged)
        {
            throw new Exception("Incomplete hardware download evidence");
        }
    }
}
