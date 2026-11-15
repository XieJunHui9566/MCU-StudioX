namespace StudioX.WorkspaceEditingValidation;

using System.Text.Json.Nodes;
using StudioX.Application.CodeIntelligence;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>真实 CMake / MX251 失败后继续使用 clangd；只操作隔离工程，不接触硬件。</summary>
internal static class FailedBuildCompletionChecks
{
    internal static async Task RunAsync(string runtime, string packs, string output)
    {
        if (Directory.Exists(output))
        {
            throw new IOException("Use a new output directory.");
        }
        Directory.CreateDirectory(output);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = timeout.Token;
        var pack = (await new PackRepository(packs).ListCatalogAsync(token)).Single(item =>
            item.Manifest.Id == "stc.ai8051u-mx251" && item.Manifest.Version == "0.2.171");
        var project = Path.Combine(output, "中文工程 with spaces");
        await new ProjectService().CreateAsync(pack, "AI8051U-34K64", "registers", "失败后补全", project, token);
        Directory.CreateDirectory(Path.Combine(project, "Hardware/Inc"));
        await File.WriteAllTextAsync(Path.Combine(project, "Hardware/Inc/probe.h"),
            "#ifndef PROBE_H\n#define PROBE_H\nstruct probe_state { unsigned char probe_member; };\n#endif\n", token);
        await File.WriteAllTextAsync(Path.Combine(project, "include/root_probe.h"), "#define ROOT_PROBE_VALUE 7\n", token);
        var cmake = Path.Combine(project, "CMakeLists.txt");
        var cmakeText = await File.ReadAllTextAsync(cmake, token);
        await File.WriteAllTextAsync(cmake, cmakeText
            .Replace("target_include_directories(app INTERFACE", "target_include_directories(app INTERFACE\n    Hardware/Inc", StringComparison.Ordinal)
            .Replace("target_compile_definitions(app INTERFACE", "target_compile_definitions(app INTERFACE\n    ANALYSIS_FROM_CMAKE=1", StringComparison.Ordinal), token);
        const string source = "#include \"probe.h\"\n#include \"root_probe.h\"\nvolatile unsigned char local_completion_value;\n" +
            "int main(void) { struct probe_state state; state.probe_member = ROOT_PROBE_VALUE; while (1) { local_completion_value = state.probe_member; } }\n";
        var main = Path.Combine(project, "src/main.c");
        await File.WriteAllTextAsync(main, source + "\n#error STUDIOX_COMPLETION_BUILD_FAILURE\n", token);
        var checks = new List<string>();
        void Check(bool passed, string message)
        {
            if (!passed)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
            Console.WriteLine("PASS " + message);
        }
        var tools = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
        var builder = new BuildService(tools);
        await using var service = new CodeIntelligenceService(runtime, Path.Combine(output, "user-data"), tools);
        try
        {
            await service.StartAsync(project, token);
            Check(service.IsReady, "language service starts before the first CMake configure");
            var failure = await builder.BuildAsync(project, cancellationToken: token);
            await File.WriteAllTextAsync(Path.Combine(output, "failed-build.log"), failure.Log, token);
            Check(!failure.Success && failure.Log.Contains("STUDIOX_COMPLETION_BUILD_FAILURE", StringComparison.Ordinal), "real compiler reports the deliberate build failure");
            Check(!File.Exists(Path.Combine(project, ".build/studiox-build-receipt.json")), "failed build leaves no downloadable success receipt");
            Check(await service.RefreshEnvironmentAsync(token) && service.IsReady, "failed build configuration reload keeps completion ready");
            await Complete("local_completion_", "local_completion_value");
            await Complete("struct probe_state state; state.", "probe_member");
            await Complete("ROOT_PROBE_", "ROOT_PROBE_VALUE");
            await Complete("ANALYSIS_FROM_", "ANALYSIS_FROM_CMAKE");

            // 加载生成清单的 Windows 分隔符、末尾斜杠与工程根目录，不依赖工作进程当前目录。
            var inputs = Path.Combine(project, ".build/mx251-cmake/mx251-inputs.json");
            var generated = await File.ReadAllTextAsync(inputs, token);
            var json = JsonNode.Parse(generated)!;
            json["includeDirectories"] = new JsonArray("include", "Hardware\\Inc\\", project + Path.DirectorySeparatorChar);
            json["sources"]![0]!["file"] = "src/main.c";
            json["sources"]![0]!["defines"] = new JsonArray("PER_FILE_ANALYSIS=1");
            await File.WriteAllTextAsync(inputs, json.ToJsonString(), token);
            Check(await service.RefreshEnvironmentAsync(token) && service.IsReady, "relative Windows paths, trailing separators and project root reload correctly");
            await Complete("struct probe_state state; state.", "probe_member");
            await Complete("PER_FILE_", "PER_FILE_ANALYSIS");

            json["includeDirectories"] = new JsonArray(Path.GetDirectoryName(project)!);
            await File.WriteAllTextAsync(inputs, json.ToJsonString(), token);
            try
            {
                await service.RefreshEnvironmentAsync(token);
                throw new InvalidOperationException("analysis accepted an include outside the project");
            }
            catch (StudioXException error) when (error.Code is "PATH_UNSAFE" or "PATH_ESCAPE")
            {
                Check(!service.IsReady, "out-of-project includes remain rejected without stale configuration");
            }
            await File.WriteAllTextAsync(inputs, generated, token);
            Check(await service.RefreshEnvironmentAsync(token) && service.IsReady, "corrected configuration automatically restores analysis");

            await File.WriteAllTextAsync(main, source, token);
            var success = await builder.BuildAsync(project, cancellationToken: token);
            await File.WriteAllTextAsync(Path.Combine(output, "recovered-build.log"), success.Log, token);
            Check(success.Success && File.Exists(Path.Combine(project, ".build/studiox-build-receipt.json")), "corrected source builds successfully with a fresh receipt");
            await service.RefreshEnvironmentAsync(token);
            await Complete("struct probe_state state; state.", "probe_member");
            await service.StopAsync();
            await service.StartAsync(project, token);
            await Complete("struct probe_state state; state.", "probe_member");
            Check(service.IsReady, "reopening the configured project preserves completion");
            await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
            {
                passed = true,
                checks,
                hardwareExecuted = false
            }, token);
        }
        finally
        {
            await service.FlushAnalysisLogAsync();
        }

        async Task Complete(string prefix, string expected)
        {
            var text = source + "\nvoid completion_probe(void) { " + prefix + "\n}\n";
            var offset = text.LastIndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
            var suggestions = await service.CompleteAsync("src/main.c", text, offset, token);
            Check(suggestions.Any(item => item.FilterText == expected || item.Label.StartsWith(expected, StringComparison.Ordinal)), "completion remains available: " + expected);
        }
    }
}
