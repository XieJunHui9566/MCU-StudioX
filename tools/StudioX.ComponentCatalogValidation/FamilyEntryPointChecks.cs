namespace StudioX.ComponentCatalogValidation;

using StudioX.Application.Health;
using StudioX.Application.Help;
using StudioX.Application.Tools;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>复用已经完成实编的副本，检查用户入口不会将组件修订误判为 SDK 改版。</summary>
internal static class FamilyEntryPointChecks
{
    internal static async Task RunAsync(string output, string components, string completedCases, Action<bool, string> check)
    {
        var catalog = new ToolsetCatalog(Path.Combine(components, "runtime/toolsets"));
        var builds = new BuildService(catalog);
        var health = new ProjectHealthService(catalog, builds);
        foreach (var id in new[] { "esp-s3", "esp-c3-rtos", "esp-p4", "esp8266" })
        {
            var caseRoot = Path.Combine(completedCases, id);
            var migration = new ComponentMigrationService(new PackRepository(Path.Combine(caseRoot, "packs")), builds);
            var name = await migration.SuggestDirectoryNameAsync(Path.Combine(caseRoot, "原工程 有空格"));
            var destination = Path.Combine(output, id, name);
            EspressifProjectPath.ValidateNewDirectory(destination);
            check(!Directory.Exists(destination) && name.All(c => c < 128 && !char.IsWhiteSpace(c)),
                id + " Unicode source suggests a usable new SDK directory without creating it");
            var report = await health.InspectAsync(Path.Combine(caseRoot, "upgrade-check"));
            await JsonStore.WriteAsync(Path.Combine(output, id + "-health.json"), report);
            check(report.CanBuild, id + " quick health accepts component revision and exact SDK: "
                + string.Join("; ", report.Checks.Where(c => c.State == HealthState.Error).Select(c => c.Code + ": " + c.Detail)));
            check(report.Checks.Any(c => c.Code == "HEALTH_SDK" && c.State == HealthState.Passed)
                && report.Checks.Any(c => c.Code == "HEALTH_TOOL_LOCK" && c.State == HealthState.Passed),
                id + " SDK identity and component content lock remain separate at the health entry");
        }
        var help = new HelpContentService();
        check(help.DiagnosticTopic("TOOLS_MIGRATION_REVIEW") == "distribution"
            && help.Get("distribution").Markdown.Contains("创建升级验证副本"), "migration review links to embedded actionable help");
        check(help.Get("esp-idf").Markdown.Contains("开发环境组件版本") && help.Get("distribution").Markdown.Contains(".studiox/migration-inputs/"),
            "embedded ESP help explains component/SDK identities and retained configuration inputs");
    }
}
