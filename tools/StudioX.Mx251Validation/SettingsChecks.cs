using System.Text.Json.Nodes;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class SettingsChecks
{
    internal static async Task RunAsync(string archive, string oldArchive, string tools, string output)
    {
        if (Directory.Exists(output))
        {
            throw new IOException("Use a new validation directory");
        }
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool ok, string label)
        {
            if (!ok)
            {
                throw new Exception(label);
            }
            checks.Add(label);
            Console.WriteLine("PASS " + label);
        }
        async Task Reject(Func<Task> action, string code)
        {
            try
            {
                await action();
            }
            catch (StudioXException ex) when (ex.Code == code) { Check(true, code); return; }
            throw new Exception("Expected " + code);
        }
        var repo = new PackRepository(Path.Combine(output, "packs"));
        var old = await repo.ImportAsync(oldArchive);
        var pack = await repo.ImportAsync(archive);
        var templates = pack.Manifest.Devices.Single().Templates;
        Check(templates.Count == 1 && templates[0].Id == "registers" && templates[0].DisplayName == "寄存器开发", "single register template");
        Check(PackCatalogPolicy.Supersedes(pack.Manifest, old.Manifest), "catalog hides superseded split templates");
        var builder = new BuildService(new ToolsetCatalog(tools));
        var service = new Mx251ProjectSettingsService(builder, new ProjectFileService());
        var project = Path.Combine(output, "工程 space");
        var manifest = await new ProjectService().CreateAsync(pack, "AI8051U-34K64", "registers", "model-test", project);
        Check(ProjectCapabilities.For(manifest).BuildSettings, "settings capability enabled");
        var settings = await service.ReadAsync(project);
        Check(settings.Model == "small" && settings.DefaultModel == "small", "new project defaults small");
        var path = Path.Combine(project, Mx251Profile.ConfigurationFile);
        var original = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var receipts = new List<object>();
        foreach (var model in new[] { "small", "large", "small" })
        {
            settings = await service.SaveModelAsync(project, settings, model);
            Check((await service.ReadAsync(project)).Model == model, model + " persisted on reopen");
            var actual = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            actual["model"] = "small";
            Check(JsonNode.DeepEquals(original, actual), "only model changed");
            var report = await builder.BuildAsync(project);
            await File.WriteAllTextAsync(Path.Combine(output, "build-" + receipts.Count + "-" + model + ".log"), report.Log);
            Check(report.Success, model + " real build");
            var receipt = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(report.Artifacts[0])!, "receipt.json")))!;
            Check(receipt["model"]!.GetValue<string>() == model, "compiler receipt uses selected model");
            receipts.Add(new
            {
                model,
                report.Artifacts,
                receipt
            });
        }
        settings = await service.SaveModelAsync(project, settings, "large");
        Check(!File.Exists(Path.Combine(project, ".build/studiox-build-receipt.json")), "model change invalidates previous build");
        await Reject(() => builder.SaveSettingsAsync(project, new(Optimization: CompilerOptimization.O3)), "MX251_BUILD_SETTINGS");
        await Reject(() => service.SaveModelAsync(project, settings, "medium"), "MX251_MODEL");
        using (builder.AcquireMaintenance())
        {
            await Reject(() => service.SaveModelAsync(project, settings, "small"), "BUILD_BUSY");
        }
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try
            {
                await service.SaveModelAsync(project, settings, "small", canceled.Token);
                throw new Exception("Expected cancellation");
            }
            catch (OperationCanceledException) { Check((await service.ReadAsync(project)).Model == "large", "cancellation preserves model"); }
        }
        var external = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        external["optimization"] = "size";
        await File.WriteAllTextAsync(path, external.ToJsonString());
        await Reject(() => service.SaveModelAsync(project, settings, "small"), "MX251_SETTINGS_CHANGED");
        external["model"] = "small";
        external["runtime"] = "native-math";
        await File.WriteAllTextAsync(path, external.ToJsonString());
        settings = await service.ReadAsync(project);
        var before = await File.ReadAllTextAsync(path);
        await Reject(() => service.SaveModelAsync(project, settings, "large"), "MX251_MODEL_RUNTIME");
        Check(await File.ReadAllTextAsync(path) == before, "incompatible runtime leaves file intact");
        external["schema"] = 1;
        external.AsObject().Remove("runtime");
        external["nativeMath"] = true;
        await File.WriteAllTextAsync(path, external.ToJsonString());
        settings = await service.ReadAsync(project);
        await Reject(() => service.SaveModelAsync(project, settings, "large"), "MX251_MODEL_RUNTIME");
        await File.WriteAllTextAsync(path, original.ToJsonString(JsonStore.Options));
        foreach (var model in new[] { "small", "large" })
        {
            var legacy = Path.Combine(output, "legacy-" + model);
            await new ProjectService().CreateAsync(old, "AI8051U-34K64", model, "legacy", legacy);
            var value = await service.ReadAsync(legacy);
            Check(value.Model == model && value.DefaultModel == model, "legacy " + model + " retains creation default");
        }
        await JsonStore.WriteAsync(Path.Combine(output, "results.json"), new
        {
            checks,
            builds = receipts,
            hardware = false
        });
    }
}
