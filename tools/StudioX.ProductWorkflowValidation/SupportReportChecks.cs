namespace StudioX.ProductWorkflowValidation;

using System.Text.Json;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

internal static class SupportReportChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var projectRoot = Path.Combine(root, "support-project");
        Directory.CreateDirectory(Path.Combine(projectRoot, ".studiox"));
        Directory.CreateDirectory(Path.Combine(projectRoot, ".build/pic/fixture"));
        await JsonStore.WriteAsync(Path.Combine(projectRoot, ".studiox/project.json"), new ProjectManifest(1, "support", "validation.pack", "1.0.0",
            new string('A', 64), "STM32F407ZGT6", "blank", "arm.gnu", "1.0.0", "arm-gnu-15.2.rel1"));
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "secret.c"), "SOURCE_MUST_STAY_PRIVATE");
        await File.WriteAllTextAsync(Path.Combine(projectRoot, ".env"), "CREDENTIAL_MUST_STAY_PRIVATE");
        await File.WriteAllTextAsync(Path.Combine(projectRoot, ".build/pic/fixture/build.log"), new string('x', 300000) + "RAW_COMPILER_FAILURE");
        await using var services = new WorkbenchService(Path.Combine(root, "support-runtime"), Path.Combine(root, "support-data"));
        var before = await File.ReadAllBytesAsync(Path.Combine(projectRoot, ".studiox/project.json"));
        var reportPath = Path.Combine(root, "support-report.json");
        var previous = Environment.GetEnvironmentVariable("CFLAGS");
        try
        {
            Environment.SetEnvironmentVariable("CFLAGS", "ENVIRONMENT_VALUE_MUST_STAY_PRIVATE");
            await services.SupportReports.ExportAsync(projectRoot, reportPath, "ORIGINAL_OPERATION_DIAGNOSTIC");
        }
        finally { Environment.SetEnvironmentVariable("CFLAGS", previous); }
        var text = await File.ReadAllTextAsync(reportPath);
        using var json = JsonDocument.Parse(text);
        var report = json.RootElement;
        check(report.GetProperty("productVersion").GetString() == typeof(WorkbenchService).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false).Cast<System.Reflection.AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "StudioXProductVersion").Value && report.GetProperty("health").GetProperty("errors").GetInt32() > 0,
            "support report includes current release identity and original failed preflight");
        check(text.Contains("RAW_COMPILER_FAILURE") && text.Contains("ORIGINAL_OPERATION_DIAGNOSTIC") &&
            report.GetProperty("files").EnumerateArray().Any(file => file.GetProperty("truncated").GetBoolean()), "support export preserves recent raw diagnostics and marks bounded log tails");
        check(!text.Contains("MUST_STAY_PRIVATE") && report.GetProperty("environment").GetProperty("ambientVariableNames").EnumerateArray().Any(name => name.GetString() == "CFLAGS"), "support export excludes source, credentials and environment values but records ambient variable names");
        var after = await File.ReadAllBytesAsync(Path.Combine(projectRoot, ".studiox/project.json"));
        check(before.SequenceEqual(after), "support export never changes project locks or configuration");
        await File.WriteAllTextAsync(Path.Combine(projectRoot, ".studiox/project.json"), "broken json");
        await services.SupportReports.ExportAsync(projectRoot, Path.Combine(root, "support-broken.json"));
        using var broken = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "support-broken.json")));
        check(broken.RootElement.GetProperty("errors").GetArrayLength() > 0 && broken.RootElement.GetProperty("files").EnumerateArray().Any(file => file.GetProperty("text").GetString() == "broken json"), "broken project still exports raw configuration and read failures");
        await services.SupportReports.ExportAsync(null, Path.Combine(root, "support-no-project.json"));
        check(File.Exists(Path.Combine(root, "support-no-project.json")), "environment diagnostics can export before a project opens");
    }
}
