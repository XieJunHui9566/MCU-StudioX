using System.Text.Json;
using ModelContextProtocol.Client;
using SkiaSharp;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Engine.Lvgl;
using StudioX.Foundation;

/// <summary>自定义源码扫描、配置权限和真实多页面渲染的独立验收。</summary>
static class CustomUiChecks
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static async Task<int> RunLegacyResourcesAsync(string project, string runtime)
    {
        var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var evidence = Path.Combine(workspace, "artifacts", "validation", "lvgl-custom-ui-current");
        Directory.CreateDirectory(evidence);
        await using var services = new WorkbenchService(runtime, Path.Combine(evidence, "machine-data"));
        var configuration = await services.LvglPreview.ReadConfigurationAsync(project);
        var report = await services.LvglPreview.ReadResourcesAsync(project);
        await File.WriteAllTextAsync(Path.Combine(evidence, "legacy-target-resources.json"), JsonSerializer.Serialize(report, JsonOptions));
        var targetEvidence = report.TargetEvidence ?? throw new InvalidOperationException("Existing project did not return target evidence.");
        var receiptExists = File.Exists(Path.Combine(project, ".build", "studiox-build-receipt.json"));
        if (receiptExists && targetEvidence.State == "NotBuilt" || !receiptExists && targetEvidence.State != "NotBuilt")
        {
            throw new InvalidOperationException("Existing project evidence does not reflect the real build receipt.");
        }
        if (!targetEvidence.MatchesCurrentUi && (report.TargetBuild.Targets.Count != 0 || string.IsNullOrWhiteSpace(targetEvidence.Message)))
        {
            throw new InvalidOperationException("Unknown target evidence must not produce numeric target usage.");
        }
        var databasePath = Path.Combine(project, ".build", "compile_commands.json");
        var missing = new List<string>();
        if (File.Exists(databasePath))
        {
            using var database = JsonDocument.Parse(await File.ReadAllTextAsync(databasePath));
            var compiled = database.RootElement.EnumerateArray().Select(item => Path.GetFullPath(item.GetProperty("file").GetString()!,
                item.TryGetProperty("directory", out var directory) ? directory.GetString()! : project)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            missing.AddRange(configuration.SourceFiles.Where(source => !compiled.Contains(Path.GetFullPath(source, project))));
            if (missing.Count > 0 && (targetEvidence.MatchesCurrentUi || report.TargetBuild.Targets.Count > 0))
            {
                throw new InvalidOperationException("An existing demo build is being reported as a UI that is not in its compilation database.");
            }
        }
        await File.WriteAllTextAsync(Path.Combine(evidence, "legacy-observed-evidence.json"), JsonSerializer.Serialize(new
        {
            success = true,
            hardware = false,
            project,
            receiptExists,
            compileDatabaseExists = File.Exists(databasePath),
            selectedUiSources = configuration.SourceFiles,
            uiSourcesAbsentFromTargetDatabase = missing,
            targetEvidence.State,
            targetEvidence.MatchesCurrentUi,
            targetEvidence.Message,
            targetCount = report.TargetBuild.Targets.Count,
            note = "Read-only regression; no target build, firmware download, or fabricated ELF."
        }, JsonOptions));
        Console.WriteLine($"PASS existing Benchmark target evidence: {targetEvidence.State}, matches={targetEvidence.MatchesCurrentUi}, targetCount={report.TargetBuild.Targets.Count}; evidence: {evidence}");
        return 0;
    }

    public static async Task<int> RunAsync(string sourceProject, string runtime, string? cliExecutable)
    {
        var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var evidence = Path.Combine(workspace, "artifacts", "validation", "lvgl-custom-ui-current");
        Directory.CreateDirectory(evidence);
        var checks = new List<string>();
        await using var services = new WorkbenchService(runtime, Path.Combine(evidence, "machine-data"));
        var scanProject = Path.Combine(evidence, "scan-project");
        var fixture = Path.Combine(evidence, "actual-ui-project");
        var externalRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "StudioXLvglCustomUiExternal"));
        var externalCreated = false;
        var authorizer = new CustomUiAuthorizer();
        try
        {
            await WriteScanProjectAsync(sourceProject, scanProject);
            var deepLibrary = Path.Combine(scanProject, "vendor", "staged", "libs", "render_engine");
            var otherLibrary = Path.Combine(scanProject, "components", "gfx_copy");
            var unsupported = Path.Combine(scanProject, "thirdparty", "newui");
            var broken = Path.Combine(scanProject, "vendor", "broken");
            await CustomUiFixtures.WriteLibraryMarkersAsync(deepLibrary);
            await CustomUiFixtures.WriteLibraryMarkersAsync(otherLibrary, patch: 5);
            await CustomUiFixtures.WriteLibraryMarkersAsync(unsupported, major: 9, minor: 0, patch: 0);
            await CustomUiFixtures.WriteLibraryMarkersAsync(broken, includeCore: false);
            Check(await services.LvglPreview.ReadConfigurationIfPresentAsync(scanProject) is null,
                "an ordinary project without preview JSON remains inspectable and explicitly unconfigured");
            await using var scanSession = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services, scanProject, authorizer));
            var definitions = await scanSession.ListToolsAsync();
            foreach (var name in new[] { "lvgl_project_discover", "lvgl_ui_inspect", "lvgl_preview_configure" })
            {
                Check(definitions.Any(tool => tool.Name == name), "real MCP discovery exposes " + name);
            }
            var discovered = Deserialize<LvglDiscoveryReport>(await scanSession.CallToolAsync("lvgl_project_discover", "{}"));
            await SaveJsonAsync("discovery.json", discovered);
            var deep = discovered.Candidates.Single(candidate => SamePath(scanProject, candidate.Directory, deepLibrary));
            Check(deep.IsSupported && deep.IsComplete && deep.Version == "8.3.11",
                "discovery finds a complete renamed LVGL root more than three folders deep");
            Check(discovered.Candidates.Count(candidate => candidate.IsSupported && candidate.IsComplete &&
                  (SamePath(scanProject, candidate.Directory, deepLibrary) || SamePath(scanProject, candidate.Directory, otherLibrary))) == 2,
                "multiple supported LVGL roots remain explicit separate candidates");
            var version9 = discovered.Candidates.Single(candidate => SamePath(scanProject, candidate.Directory, unsupported));
            Check(version9.Version == "9.0.0" && !version9.IsSupported && version9.Diagnostics.Count > 0,
                "LVGL 9 is discovered with its version and explicitly marked unsupported");
            var incomplete = discovered.Candidates.Single(candidate => SamePath(scanProject, candidate.Directory, broken));
            Check(!incomplete.IsComplete && incomplete.MissingFiles.Any(file => file.EndsWith("lv_obj.c", StringComparison.Ordinal)),
                "missing LVGL core sources remain an incomplete candidate with actionable missing files");
            await CustomUiFixtures.WriteLibraryMarkersAsync(Path.Combine(scanProject, "ui", "thirdparty", "embedded_core"));
            var inspected = Deserialize<LvglUiInspection>(await scanSession.CallToolAsync("lvgl_ui_inspect", JsonSerializer.Serialize(new
            {
                libraryDirectory = Relative(scanProject, deepLibrary),
                uiDirectory = "ui"
            })));
            await SaveJsonAsync("scan-inspection.json", inspected);
            Check(inspected.EntryPoints.Any(entry => entry.Name == "scan_ui_start") &&
                  inspected.SourceFiles.Any(path => path.EndsWith("dashboard.c", StringComparison.Ordinal)),
                "UI inspection identifies the actual entry definition and UI source");
            Check(inspected.Diagnostics.Any(item => item.Code == "UI_HARDWARE_SOURCE" && item.Path?.EndsWith("board.c", StringComparison.Ordinal) == true) &&
                  inspected.Diagnostics.Any(item => item.Code == "UI_MAIN_SOURCE" && item.Path?.EndsWith("main.c", StringComparison.Ordinal) == true) &&
                  !inspected.SourceFiles.Any(path => Path.GetFileName(path) is "board.c" or "main.c"),
                "hardware and independent main sources receive path-specific diagnostics and are excluded from suggested UI sources");
            Check(inspected.Diagnostics.Any(item => item.Code == "UI_EMBEDDED_LIBRARY") &&
                  !inspected.SourceFiles.Any(path => path.Contains("embedded_core/", StringComparison.Ordinal)),
                "a library embedded in the UI directory is diagnosed and excluded from suggested UI sources");
            Check(authorizer.Requests.Count == 0, "project discovery and UI inspection require no mutation approval");

            var externalLibrary = Path.Combine(externalRoot, "renamed_external_core");
            if (Directory.Exists(externalRoot))
            {
                throw new IOException("External scan fixture already exists; refusing to replace or clean an unowned temporary directory.");
            }
            Directory.CreateDirectory(externalRoot);
            externalCreated = true;
            await CustomUiFixtures.WriteLibraryMarkersAsync(externalLibrary);
            var externalDenied = Parse(await scanSession.CallToolAsync("lvgl_project_discover", JsonSerializer.Serialize(new
            {
                directory = externalRoot
            })));
            Check(HasCode(externalDenied, "MCP_EXTERNAL_GRANT") && authorizer.Requests.Count == 0,
                "discovery refuses an ungranted external absolute directory without reading it");
            authorizer.Allow = true;
            var opened = Parse(await scanSession.CallToolAsync("external_project_open", JsonSerializer.Serialize(new
            {
                directory = externalRoot
            })));
            var granted = Deserialize<LvglDiscoveryReport>(await scanSession.CallToolAsync("lvgl_project_discover", JsonSerializer.Serialize(new
            {
                externalRootId = opened.GetProperty("rootId").GetString(),
                directory = ""
            })));
            Check(granted.Candidates.Any(candidate => SamePath(scanProject, candidate.Directory, externalLibrary)) &&
                  authorizer.Requests.Last().Permission == StudioXMcpPermission.ExternalRead,
                "explicit ExternalRead grant enables discovery only in the selected reference root");
            if (!Path.GetPathRoot(scanProject)!.Equals(Path.GetPathRoot(externalLibrary), StringComparison.OrdinalIgnoreCase))
            {
                Check(granted.Candidates.Any(candidate => SamePath(scanProject, candidate.Directory, externalLibrary) && Path.IsPathFullyQualified(candidate.Directory)) &&
                  granted.Diagnostics.Concat(granted.Candidates.SelectMany(candidate => candidate.Diagnostics)).Any(item => item.Code == "LVGL_LIBRARY_VOLUME"),
                "cross-volume discovery retains the actual candidate path and explains same-volume import before configuration");
            }
            authorizer.Allow = false;
            var escapedUi = Parse(await scanSession.CallToolAsync("lvgl_ui_inspect", JsonSerializer.Serialize(new
            {
                libraryDirectory = Relative(scanProject, deepLibrary),
                uiDirectory = Relative(scanProject, sourceProject)
            })));
            Check(HasCode(escapedUi, "MCP_LVGL_UI_PATH"), "UI inspection keeps source selection inside the current project");

            var scanConfiguration = new LvglPreviewConfiguration(1, Relative(scanProject, deepLibrary), "ui/lv_conf_pc.h",
                ["ui/dashboard.c"], ["ui"], "scan_ui_start", Width: 120, Height: 80, Zoom: 1, AutoRebuild: false, UiDirectory: "ui");
            var control = await services.LvglPreview.ValidateSetupAsync(scanProject, scanConfiguration);
            Check(control.IsValid, "a complete supported scan fixture with a real UI entry is accepted as a valid setup");
            await InvalidSetupAsync(scanProject, scanConfiguration with
            {
                LvglDirectory = Relative(scanProject, unsupported)
            },
                "unsupported library version cannot become a valid preview configuration", "LVGL_VERSION");
            await InvalidSetupAsync(scanProject, scanConfiguration with
            {
                LvglDirectory = Relative(scanProject, broken)
            },
                "a missing core cannot become a valid preview configuration", "LVGL_LIBRARY_INCOMPLETE");
            await InvalidSetupAsync(scanProject, scanConfiguration with
            {
                SourceFiles = ["ui/dashboard.c", Relative(scanProject, Path.Combine(deepLibrary, "src", "core", "lv_obj.c"))]
            },
                "listing library core sources a second time is rejected before compilation", "LVGL_DUPLICATE_CORE");
            await InvalidSetupAsync(scanProject, scanConfiguration with
            {
                IncludeDirectories = ["ui", Relative(scanProject, Path.Combine(otherLibrary, "src"))]
            },
                "an include path into a different LVGL copy is rejected before compilation", "LVGL_MULTIPLE_LIBRARIES");
            await InvalidSetupAsync(scanProject, scanConfiguration with
            {
                EntryPoint = "missing_ui_start"
            },
                "missing UI entry receives a diagnostic before native execution", "UI_ENTRY_MISSING");
            await InvalidSetupAsync(scanProject, scanConfiguration with
            {
                ResourceDirectories = ["missing-assets"]
            },
                "missing resource directory is rejected rather than silently omitted", "LVGL_RESOURCE_MISSING");

            await CustomUiFixtures.WriteActualUiAsync(services, sourceProject, fixture);
            var existing = await services.LvglPreview.ReadConfigurationAsync(sourceProject);
            var library = Path.GetFullPath(Path.Combine(sourceProject, existing.LvglDirectory));
            var config = new LvglPreviewConfiguration(1, Path.GetRelativePath(fixture, library).Replace('\\', '/'), "include/lv_conf_pc.h",
                ["src/dashboard.c", "src/pages/details.cpp", "assets/images/custom_icon.c"], ["include"], "custom_ui_start",
                Width: 120, Height: 80, Zoom: 1, AutoRebuild: true, ResourceDirectories: ["assets/runtime"], UiDirectory: ".");
            // 测试初始选中的共享库身份；后续修改全部通过真实 MCP configure 验证授权与写入。
            await JsonStore.WriteAsync(Path.Combine(fixture, ".studiox", "lvgl-preview.json"), config with
            {
                Width = 121
            });
            await using var session = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services, fixture, authorizer));
            var actualInspection = Deserialize<LvglUiInspection>(await session.CallToolAsync("lvgl_ui_inspect", JsonSerializer.Serialize(new
            {
                libraryDirectory = config.LvglDirectory,
                uiDirectory = "."
            })));
            await SaveJsonAsync("actual-inspection.json", actualInspection);
            Check(actualInspection.SourceFiles.Any(path => path.EndsWith("details.cpp", StringComparison.Ordinal)) &&
                  actualInspection.SourceFiles.Any(path => path.EndsWith("custom_icon.c", StringComparison.Ordinal)) &&
                  actualInspection.EntryPoints.Any(entry => entry.Name == "custom_ui_start") &&
                  actualInspection.ResourceDirectories.Contains("assets/runtime", StringComparer.Ordinal),
                "custom UI inspection keeps nested C++ pages, image source and explicit C entry");
            var savedPath = Path.Combine(fixture, ".studiox", "lvgl-preview.json");
            var originalBytes = await File.ReadAllBytesAsync(savedPath);
            var configureArguments = JsonSerializer.Serialize(new
            {
                configurationJson = JsonSerializer.Serialize(config)
            });
            var denied = Parse(await session.CallToolAsync("lvgl_preview_configure", configureArguments));
            Check(HasCode(denied, "MCP_APPROVAL_DENIED") && await SameFileAsync(savedPath, originalBytes) &&
                  authorizer.Requests.Last().Permission == StudioXMcpPermission.FileWrite,
                "MCP configuration requires FileWrite approval and denial preserves the existing file");
            var requestCount = authorizer.Requests.Count;
            await using (var unsaved = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services, fixture, authorizer, () => Task.FromResult(true))))
            {
                var blocked = Parse(await unsaved.CallToolAsync("lvgl_preview_configure", configureArguments));
                Check(HasCode(blocked, "MCP_UNSAVED_FILES") && authorizer.Requests.Count == requestCount &&
                      await SameFileAsync(savedPath, originalBytes),
                    "unsaved editor state blocks configure before approval and leaves configuration intact");
            }
            var invalidConfiguration = Parse(await session.CallToolAsync("lvgl_preview_configure", JsonSerializer.Serialize(new
            {
                configurationJson = JsonSerializer.Serialize(config with
                {
                    ResourceDirectories = ["missing-runtime-assets"]
                })
            })));
            Check(!invalidConfiguration.GetProperty("saved").GetBoolean() && authorizer.Requests.Count == requestCount &&
                  await SameFileAsync(savedPath, originalBytes),
                "invalid resource mapping returns validation details before approval and preserves configuration");
            authorizer.Allow = true;
            var configured = Parse(await session.CallToolAsync("lvgl_preview_configure", configureArguments));
            Check(configured.GetProperty("saved").GetBoolean() && !services.LvglPreview.GetSnapshot(fixture).IsRunning,
                "approved configuration is saved without compiling or executing UI code");
            var saved = await services.LvglPreview.ReadConfigurationAsync(fixture);
            Check(saved.Width == 120 && !Path.IsPathFullyQualified(saved.LvglDirectory) && !Path.IsPathFullyQualified(saved.ConfigurationHeader) &&
                  saved.SourceFiles.Concat(saved.IncludeDirectories).Concat(saved.ResourceDirectories ?? []).All(path => !Path.IsPathFullyQualified(path)) &&
                  SamePath(fixture, saved.LvglDirectory, library),
                "persisted library, source, header and resource paths are relative and resolve to the selected assets");
            var started = Parse(await session.CallToolAsync("lvgl_preview_start", "{}"));
            if (!started.TryGetProperty("IsRunning", out var running) || !running.GetBoolean())
            {
                throw new InvalidOperationException("Custom UI failed to start: " + started.GetRawText());
            }
            Check(started.GetProperty("IsRunning").GetBoolean(), "custom C/C++ pages, lvgl.h and lvgl/lvgl.h include styles build and run through actual MCP");
            await Task.Delay(250);
            var first = await ScreenshotAsync("dashboard.png");
            using (var bitmap = SKBitmap.Decode(first) ?? throw new InvalidDataException("Missing dashboard PNG."))
            {
                var background = bitmap.GetPixel(118, 78);
                var image = bitmap.GetPixel(103, 8);
                Check(background.Red is >= 15 and <= 25 && background.Green is >= 35 and <= 45 && background.Blue is >= 55 and <= 65,
                    "first page reads the staged runtime resource from the preserved project-relative directory");
                Check(image.Green > 240 && image.Red < 10 && image.Blue < 10,
                    "separate C image asset produces the expected pixels in the actual framebuffer");
            }
            _ = await session.CallToolAsync("lvgl_preview_input", "{\"type\":\"pointer\",\"x\":40,\"y\":25,\"pressed\":true}");
            _ = await session.CallToolAsync("lvgl_preview_input", "{\"type\":\"pointer\",\"x\":40,\"y\":25,\"pressed\":false}");
            await Task.Delay(150);
            using (var bitmap = SKBitmap.Decode(await ScreenshotAsync("details.png")) ?? throw new InvalidDataException("Missing details PNG."))
            {
                var pixel = bitmap.GetPixel(118, 78);
                Check(pixel.Red is >= 195 and <= 210 && pixel.Blue is >= 195 and <= 210 && pixel.Green is >= 15 and <= 25,
                    "real pointer input switches from C dashboard to the custom C++ details page");
            }
            var report = Parse(await session.CallToolAsync("lvgl_resource_report", "{}"));
            await File.WriteAllTextAsync(Path.Combine(evidence, "resources.json"), report.GetRawText());
            Check(report.GetProperty("TargetBuild").GetProperty("Targets").GetArrayLength() == 0 &&
                  !report.GetProperty("TargetEvidence").GetProperty("MatchesCurrentUi").GetBoolean() &&
                  !string.IsNullOrWhiteSpace(report.GetProperty("TargetEvidence").GetProperty("Message").GetString()),
                "a custom UI without target firmware evidence reports unknown target Flash/RAM explicitly");
            Check(report.GetProperty("DisplayTransfer").GetProperty("FullFrameTransportLimitFps").ValueKind == JsonValueKind.Null,
                "unmeasured display bandwidth leaves the MCU transfer FPS unknown");

            var runtimeAsset = Path.Combine(fixture, "assets", "runtime", "banner.bin");
            await File.WriteAllTextAsync(runtimeAsset, "custom-resource-new");
            var until = DateTime.UtcNow.AddSeconds(25);
            var refreshed = false;
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(250);
                var state = services.LvglPreview.GetSnapshot(fixture);
                if (state is not { State: "Running", IsRunning: true, IsStale: false })
                {
                    continue;
                }
                using var bitmap = SKBitmap.Decode(await ScreenshotAsync("resource-updated.png")) ?? throw new InvalidDataException("Missing refreshed PNG.");
                var pixel = bitmap.GetPixel(118, 78);
                if (pixel.Red is >= 75 and <= 85 && pixel.Green is >= 95 and <= 105 && pixel.Blue is >= 115 and <= 125)
                {
                    refreshed = true;
                    break;
                }
            }
            Check(refreshed, "saving a mapped runtime asset automatically rebuilds and updates actual window pixels");
            await JsonStore.WriteAsync(savedPath, config with
            {
                ResourceDirectories = ["missing-runtime-assets"]
            });
            await File.WriteAllTextAsync(runtimeAsset, "custom-resource-ok");
            var invalidRestart = Parse(await session.CallToolAsync("lvgl_preview_start", "{}"));
            Check(HasCode(invalidRestart, "LVGL_RESOURCE_MISSING") && services.LvglPreview.GetSnapshot(fixture).IsRunning,
                "a bad resource mapping fails startup while preserving the already running preview");
            using (var bitmap = SKBitmap.Decode(await ScreenshotAsync("resource-failure-preserved.png")) ?? throw new InvalidDataException("Missing preserved PNG."))
            {
                var pixel = bitmap.GetPixel(118, 78);
                Check(pixel.Red is >= 75 and <= 85 && pixel.Green is >= 95 and <= 105 && pixel.Blue is >= 115 and <= 125,
                    "failed resource replacement preserves the prior window and staged resource content for screenshots");
            }
            _ = await session.CallToolAsync("lvgl_preview_stop", "{}");
            await JsonStore.WriteAsync(savedPath, config);
            var previewBuild = Path.Combine(fixture, ".build", "pc-preview");
            Check(!Directory.Exists(Path.Combine(previewBuild, "resources-active-a")) &&
                  !Directory.Exists(Path.Combine(previewBuild, "resources-active-b")) &&
                  !Directory.Exists(Path.Combine(previewBuild, "resources-next")),
                "stop recycles both active resource slots without retaining a resource history");

            if (cliExecutable is not null)
            {
                await using var external = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
                {
                    Command = Path.GetFullPath(cliExecutable),
                    Arguments = ["mcp", fixture, runtime],
                    Name = "StudioX custom UI external MCP"
                }));
                var tools = await external.ListToolsAsync();
                Check(new[] { "lvgl_project_discover", "lvgl_ui_inspect", "lvgl_preview_configure" }.All(name => tools.Any(tool => tool.ProtocolTool.Name == name)),
                    "external stdio MCP exposes the same custom UI tools");
            }
            await SaveJsonAsync("result.json", new
            {
                success = true,
                hardware = false,
                checks,
                sourceProject,
                fixture,
                copiedLibraries = false,
                copiedToolchain = false
            });
            Console.WriteLine($"PASS {checks.Count} custom UI checks; evidence: {evidence}");
            return 0;

            async Task<byte[]> ScreenshotAsync(string name)
            {
                var frame = await session.CallToolDetailedAsync("lvgl_preview_screenshot", "{}");
                if (frame.Images.Count != 1)
                {
                    throw new InvalidDataException(frame.Text);
                }
                var image = frame.Images.Single();
                if (image.MimeType != "image/png")
                {
                    throw new InvalidDataException("Screenshot is not PNG.");
                }
                await File.WriteAllBytesAsync(Path.Combine(evidence, name), image.Data);
                return image.Data;
            }
        }
        catch (Exception ex)
        {
            await SaveJsonAsync("result.json", new
            {
                success = false,
                hardware = false,
                checks,
                error = ex.ToString()
            });
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            try
            {
                await services.LvglPreview.StopAsync(fixture);
            }
            finally { if (externalCreated) { DeleteExternalFixture(externalRoot); } }
        }

        void Check(bool condition, string description)
        {
            if (!condition)
            {
                throw new InvalidOperationException("FAIL: " + description);
            }
            checks.Add(description);
            Console.WriteLine("PASS: " + description);
        }
        Task SaveJsonAsync(string name, object value) => File.WriteAllTextAsync(Path.Combine(evidence, name), JsonSerializer.Serialize(value, JsonOptions));
        async Task InvalidSetupAsync(string project, LvglPreviewConfiguration configuration, string description, string? code = null)
        {
            var validation = await services.LvglPreview.ValidateSetupAsync(project, configuration);
            Check(!validation.IsValid && validation.Diagnostics.Any(item => item.Severity.Equals("error", StringComparison.OrdinalIgnoreCase) &&
                  !string.IsNullOrWhiteSpace(item.Message) && (code is null || item.Code == code)), description);
        }
    }

    private static async Task WriteScanProjectAsync(string sourceProject, string project)
    {
        Directory.CreateDirectory(Path.Combine(project, ".studiox"));
        Directory.CreateDirectory(Path.Combine(project, "ui"));
        File.Copy(Path.Combine(sourceProject, ".studiox", "project.json"), Path.Combine(project, ".studiox", "project.json"), true);
        CustomUiFixtures.CopyDeviceMetadata(sourceProject, project);
        await File.WriteAllTextAsync(Path.Combine(project, "ui", "dashboard.c"), "#include \"lvgl.h\"\nvoid scan_ui_start(void) { lv_obj_create(lv_scr_act()); }\n");
        await File.WriteAllTextAsync(Path.Combine(project, "ui", "board.c"), "#include \"ch32v30x.h\"\nvoid board_init(void) { GPIO_Init(GPIOA, 0); }\n");
        await File.WriteAllTextAsync(Path.Combine(project, "ui", "main.c"), "int main(void) { return 0; }\n");
        await File.WriteAllTextAsync(Path.Combine(project, "ui", "lv_conf_pc.h"), "#ifndef LV_CONF_H\n#define LV_CONF_H\n#define LV_COLOR_DEPTH 16\n#endif\n");
    }
    private static T Deserialize<T>(string text)
    {
        if (Parse(text).TryGetProperty("error", out _))
        {
            throw new InvalidDataException(text);
        }
        return JsonSerializer.Deserialize<T>(text, JsonOptions) ?? throw new InvalidDataException(text);
    }
    private static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement.Clone();
    private static bool HasCode(JsonElement value, string code) => value.TryGetProperty("code", out var field) && field.GetString() == code;
    private static bool SamePath(string project, string candidate, string expected) =>
        Path.GetFullPath(Path.Combine(project, candidate)).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
    private static string Relative(string project, string path) => Path.GetRelativePath(project, path).Replace('\\', '/');
    private static async Task<bool> SameFileAsync(string path, byte[] expected)
    {
        var actual = await File.ReadAllBytesAsync(path);
        return expected.AsSpan().SequenceEqual(actual);
    }
    private static void DeleteExternalFixture(string directory)
    {
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "StudioXLvglCustomUiExternal"));
        if (!Path.GetFullPath(directory).Equals(allowed, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("External scan fixture cleanup escaped its exact owned temporary directory.");
        }
        if (!Directory.Exists(directory))
        {
            return;
        }
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(directory));
        while (pending.TryPop(out var current))
        {
            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("External scan fixture cleanup encountered a directory reparse point.");
            }
            foreach (var item in current.EnumerateFileSystemInfos())
            {
                if (!item.FullName.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new IOException("External scan fixture cleanup encountered an unexpected target.");
                }
                if (item is DirectoryInfo child)
                {
                    pending.Push(child);
                }
            }
        }
        Directory.Delete(directory, true);
    }
    private sealed class CustomUiAuthorizer : IStudioXMcpAuthorizer
    {
        public bool Allow
        {
            get; set;
        }
        public List<StudioXMcpApprovalRequest> Requests { get; } = [];
        public Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(Allow && request.Permission is StudioXMcpPermission.FileWrite or StudioXMcpPermission.Build or StudioXMcpPermission.ExternalRead);
        }
    }
}
