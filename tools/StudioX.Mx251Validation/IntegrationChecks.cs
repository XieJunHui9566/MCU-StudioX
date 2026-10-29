using System.Text.Json.Nodes;
using StudioX.Application;
using StudioX.Application.Health;
using StudioX.Application.Tools;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class IntegrationChecks
{
    internal static async Task RunAsync(string directory)
    {
        var root = Path.GetFullPath(directory);
        var catalog = new ToolsetCatalog(Path.Combine(root, "移动后 工具组件"));
        var builder = new BuildService(catalog);
        var project = Path.Combine(root, "工程 空格/small");
        var data = Path.Combine(root, "ui-data");
        Directory.CreateDirectory(data);
        var management = new ToolManagementService(catalog, new PackRepository(Path.Combine(root, "packs")), new RecentProjectService(data), data);
        var plan = await new ProjectToolPreparationService(catalog, management).InspectAsync(project);
        if (plan.Requirements.Count != 1 || plan.Requirements[0].State != ProjectToolState.Installed)
        {
            throw new InvalidOperationException("Tool preparation incorrectly requires GCC/CMake");
        }
        var health = new ProjectHealthService(catalog, builder);
        var quick = await health.InspectAsync(project);
        var deep = await health.InspectAsync(project, deep: true);
        if (quick.Errors != 0 || deep.Errors != 0 || !quick.CanBuild || !deep.CanBuild)
        {
            throw new InvalidOperationException(quick.ToText() + deep.ToText());
        }
        var memory = await new BuildMemoryService(catalog).ReadAsync(project);
        await JsonStore.WriteAsync(Path.Combine(root, "ui-integration.json"), new
        {
            plan,
            quick,
            deep,
            memory
        });
        Console.WriteLine("PASS component preparation, quick/deep health, explicit memory scope");

        // 实际汇编输入在构建中变化，当前成功凭据必须失效；不用假进程代替真实流水线。
        var fixture = Path.Combine(root, "固定工程/full-math");
        var configPath = Path.Combine(fixture, Mx251Profile.ConfigurationFile);
        var prior = await File.ReadAllBytesAsync(configPath);
        var assembly = Path.Combine(fixture, "source_guard.asm");
        if (File.Exists(assembly))
        {
            throw new IOException("Fixture already modified");
        }
        await File.WriteAllTextAsync(assembly, ".area CSEG (CODE)\n");
        var config = JsonNode.Parse(prior)!;
        config["files"]!.AsArray().Add("source_guard.asm");
        config["units"]!.AsArray().Add(JsonNode.Parse("""{"name":"source_guard","file":"source_guard.asm","backend":"legacy","stackAuto":true,"defines":[]}"""));
        config["link"]!.AsArray().Add("source_guard");
        await File.WriteAllTextAsync(configPath, config.ToJsonString());
        var mutation = Task.Run(async () =>
        {
            await Task.Delay(1500);
            await File.AppendAllTextAsync(assembly, "; modified during build\n");
        });
        var rejected = false;
        try
        {
            await builder.BuildAsync(fixture);
        }
        catch (StudioXException error) when (error.Code == "MX251_SOURCE_CHANGED") { rejected = true; }
        finally
        {
            await mutation;
            await File.WriteAllBytesAsync(configPath, prior);
            File.Delete(assembly);
        }
        if (!rejected || File.Exists(Path.Combine(fixture, ".build/studiox-build-receipt.json")))
        {
            throw new InvalidOperationException("Changed assembly input was not rejected");
        }
        await JsonStore.WriteAsync(Path.Combine(root, "source-change-check.json"), new
        {
            assemblyMutationRejected = true,
            receiptInvalidated = true
        });
        Console.WriteLine("PASS actual assembly mutation invalidates publication");
    }
}
