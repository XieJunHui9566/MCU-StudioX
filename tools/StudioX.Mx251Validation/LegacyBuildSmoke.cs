using System.Text.Json.Nodes;
using StudioX.Engine;
using StudioX.Foundation;

internal static class LegacyBuildSmoke
{
    internal static async Task RunAsync(string directory, string installedTools)
    {
        var root = Path.GetFullPath(directory);
        var project = Path.Combine(root, "legacy-smoke");
        Directory.CreateDirectory(Path.Combine(project, ".studiox"));
        Directory.CreateDirectory(Path.Combine(project, "device"));
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "projects/ide/ai-fixed-calls/.studiox/project.json")))!;
        manifest["toolsetVersion"] = Mx251Profile.Version;
        manifest["compilerId"] = Mx251Profile.CompilerId;
        manifest["developmentComponents"]![0]!["version"] = Mx251Profile.Version;
        manifest["developmentComponents"]![0]!["compilerId"] = Mx251Profile.CompilerId;
        await File.WriteAllTextAsync(Path.Combine(project, ".studiox/project.json"), manifest.ToJsonString());
        File.Copy(Path.Combine(root, "projects/ide/ai-fixed-calls/device/manifest.json"), Path.Combine(project, "device/manifest.json"), true);
        var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "projects/ide/ai-fixed-calls/mx251.json")))!;
        config["backend"] = "ordinary-wide";
        config["model"] = "small";
        config["runtime"] = "standard";
        config["defines"] = new JsonArray();
        await File.WriteAllTextAsync(Path.Combine(project, "mx251.json"), config.ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(project, "main.c"), "volatile unsigned long counter; void main(void){counter=0x12345678UL;for(;;){counter++;}}\n");
        await File.WriteAllTextAsync(Path.Combine(project, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.25)\nproject(legacy NONE)\ninclude(\"${MX251_CMAKE_MODULE}\")\nadd_library(app INTERFACE)\ntarget_sources(app INTERFACE main.c)\nmx251_firmware(app)\n");
        var report = await new BuildService(new ToolsetCatalog(installedTools)).BuildAsync(project);
        await File.WriteAllTextAsync(Path.Combine(root, "legacy-smoke.log"), report.Log);
        if (!report.Success)
        {
            throw new InvalidOperationException("Legacy build failed");
        }
        var memory = await new BuildMemoryService().ReadAsync(project);
        if (memory.Targets.Count == 0 || memory.Targets.Any(t => t.Diagnostic is not null))
        {
            throw new InvalidOperationException("Legacy memory analysis failed");
        }
        await JsonStore.WriteAsync(Path.Combine(root, "legacy-smoke-results.json"), new
        {
            complete = true,
            report.Success,
            memory,
            hardware = false
        });
        Console.WriteLine("PASS installed legacy component read-only build and memory smoke");
    }
}
