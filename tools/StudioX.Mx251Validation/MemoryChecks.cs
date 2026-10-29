using System.Security.Cryptography;
using System.Text.Json.Nodes;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class MemoryChecks
{
    internal static async Task RunAsync(string archive, string toolRoot, string output, string originalProject)
    {
        if (Directory.Exists(output))
        {
            throw new IOException("Use a new validation directory");
        }
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        var reports = new List<object>();
        void Check(bool value, string name)
        {
            if (!value)
            {
                throw new Exception(name);
            }
            checks.Add(name);
            Console.WriteLine("PASS " + name);
        }
        var catalog = new ToolsetCatalog(toolRoot);
        var builder = new BuildService(catalog);
        var memory = new BuildMemoryService(catalog);
        var history = new BuildHistoryService(Path.Combine(output, "user-data"), memory);
        var pack = await new PackRepository(Path.Combine(output, "packs")).ImportAsync(archive);
        var tools = await catalog.ResolveAsync(Mx251Profile.ToolsetId, Mx251Profile.Version, Mx251Profile.CompilerId);
        var project = Path.Combine(output, "template");
        await new ProjectService().CreateAsync(pack, "AI8051U-34K64", "registers", "memory", project);
        async Task<BuildMemoryReport> Build(string root, string label)
        {
            var report = await builder.BuildAsync(root);
            await File.WriteAllTextAsync(Path.Combine(output, label + ".log"), report.Log);
            Check(report.Success, label + " build");
            var result = await memory.ReadAsync(root);
            Check(result.Targets.Count == 1 && result.Targets[0].Regions.Count == 3, label + " memory: " + result.Message);
            var snapshot = await history.CaptureAsync(root, 1.0);
            Check((await history.ListAsync(root)).Last().Id == snapshot.Id, label + " history saved");
            reports.Add(new
            {
                label,
                result,
                report.Artifacts
            });
            return result;
        }
        var small = await Build(project, "small");
        Check(small.Targets[0].Regions.Select(r => r.Used).SequenceEqual(new ulong[] { 263, 5, 1032 }), "known template: CODE263 XDATA5 EDATA8+1024, no double-counted REG_BANK_0");
        Check(small.Targets[0].Regions.Select(r => r.Capacity).SequenceEqual(new ulong[] { 65536, 28672, 2048 }), "physical CODE/EDATA and actual XDATA link budget");
        var first = (await history.ListAsync(project)).Last();
        var settingsService = new Mx251ProjectSettingsService(builder, new ProjectFileService());
        var settings = await settingsService.ReadAsync(project);
        await settingsService.SaveModelAsync(project, settings, "large");
        Check((await memory.ReadAsync(project)).Targets.Count == 0, "model change clears old statistics");
        await Build(project, "large");
        var last = (await history.ListAsync(project)).Last();
        Check(BuildHistoryService.Compare(first, last).Count == 3, "history compares memory regions across models");
        var receiptPath = Path.Combine(project, ".build/studiox-build-receipt.json");
        var originalReceipt = await File.ReadAllTextAsync(receiptPath);
        var receipt = JsonNode.Parse(originalReceipt)!;
        var mapRelative = receipt["analysisInputs"]!.AsObject().Select(p => p.Key).Single(p => p.EndsWith(".map"));
        var mapPath = Path.Combine(project, mapRelative);
        var map = await File.ReadAllTextAsync(mapPath);
        var stamp = File.GetLastWriteTimeUtc(mapPath);
        await File.WriteAllTextAsync(mapPath, map.Replace("00000400", "00000500"));
        File.SetLastWriteTimeUtc(mapPath, stamp);
        Check((await memory.ReadAsync(project)).Targets.Count == 0, "same-size same-time MAP mutation rejected by hash");
        await File.WriteAllTextAsync(mapPath, map);
        receipt.AsObject().Remove("analysisInputs");
        await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());
        Check((await memory.ReadAsync(project)).Message.Contains("重新编译"), "legacy receipt requests rebuild instead of inventing statistics");
        await File.WriteAllTextAsync(receiptPath, originalReceipt);
        var memRelative = JsonNode.Parse(originalReceipt)!["analysisInputs"]!.AsObject().Select(p => p.Key).Single(p => p.EndsWith(".mem"));
        var memPath = Path.Combine(project, memRelative);
        var mem = await File.ReadAllBytesAsync(memPath);
        await File.WriteAllTextAsync(memPath, System.Text.Encoding.UTF8.GetString(mem).Replace("EDATA capacity=2048", "EDATA capacity=512"));
        receipt = JsonNode.Parse(originalReceipt)!;
        receipt["analysisInputs"]![memRelative] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(memPath)));
        await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());
        Check((await memory.ReadAsync(project)).Targets.Count == 0, "self-consistent hash cannot hide contradictory stack capacity");
        await File.WriteAllBytesAsync(memPath, mem);
        await File.WriteAllTextAsync(receiptPath, originalReceipt);
        Check((await memory.ReadAsync(project)).Targets.Count == 1, "restored inputs reopen without cached failure");
        var source = Path.Combine(project, "src/main.c");
        var text = await File.ReadAllTextAsync(source);
        await File.WriteAllTextAsync(source, text + "\n#error EXPECTED_ANALYZER_FAILURE\n");
        Check((await memory.ReadAsync(project)).Targets.Count == 0, "source change rejects stale memory");
        Check(!(await builder.BuildAsync(project)).Success && (await memory.ReadAsync(project)).Targets.Count == 0, "failed build clears statistics");
        await File.WriteAllTextAsync(source, text);
        await Build(project, "recovered");
        foreach (var name in new[] { "small-wide-poison1-size", "large-wide-poison1-size", "full-math", "final-native-small-speed", "final-ordinary-calls-large-stack-auto-speed-r87" })
        {
            var root = Path.Combine(output, name);
            await new ProjectService().CreateAsync(pack, "AI8051U-34K64", "registers", "fixed", root);
            var sample = Path.Combine(tools.RootDirectory, "examples/fixed", name);
            foreach (var file in Directory.EnumerateFiles(sample, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(root, Path.GetRelativePath(sample, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, true);
            }
            File.Copy(Path.Combine(sample, "project.json"), Path.Combine(root, Mx251Profile.ConfigurationFile), true);
            var result = await Build(root, name);
            if (name == "small-wide-poison1-size")
            {
                Check(result.Targets[0].Regions[2].Used == 1137, "initialized data/bit/idata banks occupy113 bytes, not DSEG span136");
            }
        }
        // 用户工程只复制到隔离目录，保留其源码、配置和原始构建。
        var copy = Path.Combine(output, "user-project");
        foreach (var file in Directory.EnumerateFiles(originalProject, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(originalProject, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(p => p is ".build" or ".git"))
            {
                continue;
            }
            var destination = Path.Combine(copy, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        await Build(copy, "user-project");
        await JsonStore.WriteAsync(Path.Combine(output, "results.json"), new
        {
            checks,
            reports,
            hardware = false
        });
    }
}
