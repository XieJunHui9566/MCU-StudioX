using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Engine;
using StudioX.Foundation;

/// <summary>通过真正的 MCP 会话读取模组规格；仅在隔离副本中保存测试配置，不构建或接触硬件。</summary>
internal static class EspressifModuleMcpChecks
{
    public static async Task RunAsync(string sourceProject)
    {
        sourceProject = Path.GetFullPath(sourceProject);
        var sourceManifest = await ProjectService.ReadAsync(sourceProject);
        if (sourceManifest.Espressif?.Target != "esp32s3")
        {
            throw new ArgumentException("验证输入必须是已有的 ESP32-S3 原生工程。");
        }
        var original = HashSources(sourceProject);
        var output = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "artifacts/validation/espressif-module-mcp-current"));
        Directory.CreateDirectory(output);
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var root = Path.GetFullPath(Path.Combine(tempRoot, "studiox-module-mcp-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        var observations = new List<object>();
        void Check(bool condition, string description)
        {
            if (!condition)
            {
                throw new InvalidOperationException(description);
            }
            checks++;
            Console.WriteLine("PASS " + description);
        }

        try
        {
            var project = Path.Combine(root, "s3");
            foreach (var relative in original.Keys)
            {
                var destination = Path.Combine(project, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(sourceProject, relative), destination);
            }
            // 输入工程可能已有选择，验证副本明确以无 sidecar 状态开始；不会删除输入文件。
            var sidecar = Path.Combine(project, EspressifModuleSettings.RelativePath);
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }
            var protectedFiles = new[] { "CMakeLists.txt", "sdkconfig", "sdkconfig.defaults" }
                .Where(relative => File.Exists(Path.Combine(project, relative)))
                .ToDictionary(relative => relative, relative => HashFile(Path.Combine(project, relative)), StringComparer.Ordinal);
            Check(protectedFiles.ContainsKey("CMakeLists.txt") && protectedFiles.ContainsKey("sdkconfig"),
                "native fixture preserves actual root CMake and sdkconfig");
            var authorizer = new DenyAuthorizer();
            await using var services = new WorkbenchService(Path.Combine(root, "runtime"), Path.Combine(root, "data"));
            await using var session = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services, project, authorizer));
            var definitions = await session.ListToolsAsync();
            Check(definitions.Count(item => item.Name == "project_info") == 1,
                "real MCP handshake exposes one project_info tool");

            async Task ReadAsync(string label, EspressifModuleSettings expected)
            {
                var text = await session.CallToolAsync("project_info", "{}");
                using var document = JsonDocument.Parse(text);
                var result = document.RootElement;
                Check(!result.TryGetProperty("error", out _), label + " succeeds through MCP");
                Check(result.GetProperty("espressif").Deserialize<EspressifProjectSettings>(JsonStore.Options)?.Target == "esp32s3",
                    label + " retains native S3 target");
                var settings = result.GetProperty("espressifModule").Deserialize<EspressifModuleSettings>(JsonStore.Options);
                Check(settings == expected, label + " returns complete bound-project module settings");
                Check(authorizer.Requests == 0, label + " requires no write authorization");
                observations.Add(new
                {
                    label,
                    response = result.Clone()
                });
            }

            await ReadAsync("missing sidecar inherits sdkconfig", new());
            foreach (var id in new[] { "esp32-s3-wroom-1-n8r8", "esp32-s3-wroom-1-n16r8" })
            {
                var preset = EspressifModuleCatalog.ForTarget("esp32s3").Single(item => item.Id == id);
                await services.Builds.SaveEspressifModuleSettingsAsync(project, preset.Settings);
                await ReadAsync(id, preset.Settings);
                Check(preset.Settings is { FlashMode: "qio", FlashFrequencyMhz: 80, PsramMode: "octal", PsramSizeMb: 8 },
                    id + " reports Quad Flash and Octal PSRAM distinctly");
                Check(protectedFiles.All(pair => HashFile(Path.Combine(project, pair.Key)) == pair.Value),
                    id + " leaves native root configuration untouched");
            }
            await services.Builds.SaveEspressifModuleSettingsAsync(project, new());
            await ReadAsync("explicit default inherits sdkconfig", new());

            var other = Path.Combine(root, "non-esp");
            await JsonStore.WriteAsync(Path.Combine(other, ".studiox/project.json"),
                new ProjectManifest(1, "module-mcp-non-esp", "test.pack", "1.0.0", "test", "test-device", "test-template", "test-tools", "1.0.0", "gcc"));
            await using var otherSession = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services, other, authorizer));
            var otherText = await otherSession.CallToolAsync("project_info", "{}");
            using var otherDocument = JsonDocument.Parse(otherText);
            var otherResult = otherDocument.RootElement;
            Check(!otherResult.TryGetProperty("error", out _) && otherResult.GetProperty("espressifModule").ValueKind == JsonValueKind.Null &&
                otherResult.GetProperty("espressif").ValueKind == JsonValueKind.Null, "non-ESP project_info returns explicit null module");
            Check(authorizer.Requests == 0, "all actual MCP reads bypass deny authorizer");
            Check(protectedFiles.All(pair => HashFile(Path.Combine(project, pair.Key)) == pair.Value),
                "all root sdkconfig/defaults/CMake hashes remain unchanged");
            var after = HashSources(sourceProject);
            Check(original.Count == after.Count && original.All(pair => after.TryGetValue(pair.Key, out var hash) && hash == pair.Value),
                "original source fixture is unchanged");
            observations.Add(new
            {
                label = "non-ESP",
                response = otherResult.Clone()
            });
            var report = new
            {
                passed = true,
                checks,
                evidence = "actual MCP client/server handshake; read-only; no hardware",
                observations
            };
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(report, JsonStore.Options));
            Console.WriteLine($"PASS {checks} Espressif module MCP checks; result: {Path.Combine(output, "result.json")}");
        }
        finally
        {
            // 删除前再次核对绝对目录只属于本次随机临时根目录，避免回收用户工程。
            var full = Path.GetFullPath(root);
            if (full.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(full).StartsWith("studiox-module-mcp-", StringComparison.Ordinal) && Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
            }
        }
    }

    private static Dictionary<string, string> HashSources(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => (Path: path, Relative: Path.GetRelativePath(root, path)))
        .Where(item => !item.Relative.StartsWith(".build" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !item.Relative.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        .ToDictionary(item => item.Relative, item => HashFile(item.Path), StringComparer.Ordinal);

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed class DenyAuthorizer : IStudioXMcpAuthorizer
    {
        public int Requests
        {
            get; private set;
        }

        public Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token)
        {
            Requests++;
            return Task.FromResult(false);
        }
    }
}
