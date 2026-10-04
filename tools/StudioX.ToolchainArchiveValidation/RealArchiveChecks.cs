namespace StudioX.ToolchainArchiveValidation;

using StudioX.Application.Tools;
using StudioX.Engine;
using StudioX.Foundation;

internal static class RealArchiveChecks
{
    internal static async Task RunAsync(string output, string archive, string originalRoot, bool verifyPayload = true)
    {
        if (Directory.Exists(output))
        {
            throw new ArgumentException("Use new output.");
        }
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool condition, string text)
        {
            if (!condition)
            {
                throw new InvalidOperationException(text);
            }
            checks.Add(text);
            Console.WriteLine("PASS " + text);
        }
        try
        {
            var original = await File.ReadAllBytesAsync(Path.Combine(originalRoot, "toolset.json"));
            using (var source = File.OpenRead(archive))
            {
                using (var container = ToolchainArchive.Open(source))
                {
                    Check(container.Container == "7z" && (await container.ReadManifestAsync()).SequenceEqual(original), "real archive uses 7z and exact original manifest bytes");
                }
            }
            var catalog = new ToolsetCatalog(Path.Combine(output, "runtime/toolsets"), Path.Combine(output, "user-data"));
            var manager = new ToolManagementService(catalog, new(Path.Combine(output, "packs")), new(output), output);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var preview = await manager.PreviewInstallAsync(archive, new ProgressLog());
            Check((await new ProjectToolPreparationService(catalog, manager).PreviewCompatibilityAsync(null, preview)).CanInstall,
                "real 7z participates in component compatibility preview");
            if (!verifyPayload)
            {
                await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
                {
                    success = true,
                    hardware = false,
                    payloadVerified = false,
                    checks,
                    preview
                });
                return;
            }
            var installed = await manager.InstallAsync(preview, new ProgressLog());
            Check(!installed.AlreadyInstalled, "real 7z install verifies complete file set and payload SHA-256");
            var duplicate = await manager.InstallAsync(await manager.PreviewInstallAsync(archive), new ProgressLog());
            Check(duplicate.AlreadyInstalled, "real solid 7z duplicate import verifies payload and installed files");
            Check((await File.ReadAllBytesAsync(Path.Combine(catalog.RootDirectory, preview.Identity.RelativeDirectory, "toolset.json"))).SequenceEqual(original),
                "installed manifest remains identical to source");
            watch.Stop();
            await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
            {
                success = true,
                hardware = false,
                checks,
                preview,
                installed,
                duplicate,
                seconds = watch.Elapsed.TotalSeconds,
                toolsRoot = catalog.RootDirectory
            });
        }
        catch (Exception error)
        {
            await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
            {
                success = false,
                hardware = false,
                checks,
                diagnostic = error.ToString()
            });
            throw;
        }
    }
    internal static async Task BuildAsync(string output, string toolsRoot, string archive, string device, string template)
    {
        if (Directory.Exists(output))
        {
            throw new ArgumentException("Use new output.");
        }
        Directory.CreateDirectory(output);
        var packs = new StudioX.Packages.PackRepository(Path.Combine(output, "packs"));
        var pack = await packs.ImportAsync(archive);
        var project = Path.Combine(output, "project");
        await new ProjectService().CreateAsync(pack, device, template, "archive_validation", project);
        var report = await new BuildService(new(toolsRoot)).BuildAsync(project, new ProgressLog());
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            success = report.Success,
            hardware = false,
            device,
            template,
            toolsRoot,
            report
        });
        if (!report.Success)
        {
            throw new InvalidOperationException(report.Log);
        }
        Console.WriteLine("PASS real firmware builds using component imported from 7z: " + device);
    }
    private sealed class ProgressLog : IProgress<string>
    {
        private long last;
        public void Report(string text)
        {
            var now = Environment.TickCount64;
            if (last == 0 || now - last >= 10000)
            {
                last = now;
                Console.WriteLine(text);
            }
        }
    }
}
