namespace StudioX.PicValidation;

using StudioX.Application;
using StudioX.Foundation;

internal static class CatalogMatrix
{
    public static async Task RunAsync(string evidence, string runtime, string archives)
    {
        if (Directory.Exists(evidence))
        {
            throw new ArgumentException("Use a new evidence directory");
        }
        Directory.CreateDirectory(evidence);
        await using var services = new WorkbenchService(runtime, Path.Combine(evidence, "data"));
        var rows = new List<object>();
        var count = 0;
        foreach (var archive in Directory.GetFiles(archives, "microchip.pic16*.mcupack").Order(StringComparer.Ordinal))
        {
            var pack = await services.Packs.ImportAsync(archive);
            foreach (var device in pack.Manifest.Devices)
            {
                var root = Path.Combine(evidence, "projects", device.Id);
                await services.Projects.CreateAsync(pack, device.Id, "xc8-basic", "PicExample", root);
                var health = await services.ProjectHealth.InspectAsync(root);
                var build = await services.Builds.BuildAsync(root);
                await File.WriteAllTextAsync(Path.Combine(evidence, device.Id + ".log"), build.Log);
                var preview = build.Success ? await services.Builds.Pic.PreviewAsync(root) : null;
                if (!health.CanBuild || !build.Success || preview is null || preview.Bytes == 0)
                {
                    throw new InvalidOperationException(device.Id + " actual XC8 compilation failed: " + build.Log);
                }
                rows.Add(new
                {
                    device.Id,
                    device.Architecture,
                    device.FlashBytes,
                    device.RamBytes,
                    device.Pic,
                    preview.Sha256,
                    preview.Bytes
                });
                Console.WriteLine("PASS " + device.Id + " · " + device.Pic!.CapacityDescription);
                count++;
            }
        }
        if (count != 122)
        {
            throw new InvalidOperationException("Expected 122 exact identities");
        }
        await JsonStore.WriteAsync(Path.Combine(evidence, "result.json"), new
        {
            passed = true,
            count,
            hardware = false,
            mplabIdeInvoked = false,
            devices = rows
        });
    }
}
