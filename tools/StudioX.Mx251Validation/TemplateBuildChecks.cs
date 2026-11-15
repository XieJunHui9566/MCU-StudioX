using System.Security.Cryptography;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class TemplateBuildChecks
{
    internal static async Task RunAsync(string tools, string sources, string output)
    {
        Directory.CreateDirectory(output);
        var repository = new PackRepository(Path.Combine(output, "packs"));
        var builder = new BuildService(new ToolsetCatalog(tools));
        var results = new List<object>();
        foreach (var id in new[] { "stc.ai8051u-mx251", "stc.stc32g-mx251" })
        {
            var archive = Path.Combine(output, id + "-0.2.171.mcupack");
            await PackArchiveWriter.WriteAsync(Path.Combine(sources, id), archive);
            var pack = await repository.ImportAsync(archive);
            var device = pack.Manifest.Devices.Single();
            foreach (var template in device.Templates)
            {
                var project = Path.Combine(output, id + "-" + template.Id);
                await new ProjectService().CreateAsync(pack, device.Id, template.Id, template.Id, project);
                var report = await builder.BuildAsync(project);
                await File.WriteAllTextAsync(project + ".log", report.Log);
                if (!report.Success)
                {
                    throw new InvalidOperationException(report.Log);
                }
                var hex = Path.Combine(project, ".build/firmware.hex");
                results.Add(new
                {
                    id,
                    template = template.Id,
                    pack.ContentHash,
                    success = true,
                    firmwareSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(hex)))
                });
                Console.WriteLine("PASS template " + id + "/" + template.Id);
            }
        }
        await JsonStore.WriteAsync(Path.Combine(output, "results.json"), results);
    }
}
