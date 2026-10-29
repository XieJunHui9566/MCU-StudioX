using System.Security.Cryptography;
using StudioX.Application;
using StudioX.Application.Health;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class PackageSmokeChecks
{
    internal static async Task RunAsync(string packages, string output)
    {
        packages = Path.GetFullPath(packages);
        output = Path.GetFullPath(output);
        if (Directory.Exists(output))
        {
            throw new IOException("Use new smoke directory");
        }
        Directory.CreateDirectory(output);
        var catalog = new ToolsetCatalog(Path.Combine(output, "工具 tools"));
        var entry = new ToolEnvironmentEntry(Mx251Profile.ToolsetId, Mx251Profile.Version, "R89", Mx251Profile.CompilerId, 0, 0, false, "");
        await new ToolEnvironmentService(catalog).RepairAsync(entry, Path.Combine(packages, "studiox.mx251-0.1.89-win-x64.mcutoolchain"), null, default, installOnly: true);
        var pack = await new PackRepository(Path.Combine(output, "packs")).ImportAsync(Path.Combine(packages, "stc.ai8051u-mx251-0.1.89.mcupack"));
        var builder = new BuildService(catalog);
        var results = new List<object>();
        foreach (var model in new[] { "small", "large" })
        {
            var root = Path.Combine(output, "新工程 space", model);
            await new ProjectService().CreateAsync(pack, "AI8051U-34K64", model, "final-" + model, root);
            var report = await builder.BuildAsync(root);
            await File.WriteAllTextAsync(Path.Combine(output, model + ".log"), report.Log);
            if (!report.Success)
            {
                throw new InvalidOperationException(report.Log);
            }
            var health = await new ProjectHealthService(catalog, builder).InspectAsync(root, deep: true);
            if (!health.CanBuild)
            {
                throw new InvalidOperationException(health.ToText());
            }
            results.Add(new
            {
                model,
                sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(report.Artifacts[0]))),
                report.Artifacts,
                health.CanBuild
            });
            Console.WriteLine("PASS final archive import + " + model + " F7 backend + health");
        }
        await JsonStore.WriteAsync(Path.Combine(output, "smoke.json"), new
        {
            results,
            hardwareConnected = false
        });
    }
}
