using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using StudioX.Application;
using StudioX.Application.Lvgl;
using StudioX.Application.Mcp;
using StudioX.Engine;
using StudioX.Engine.Lvgl;
using StudioX.Foundation;
using SkiaSharp;

/// <summary>内置链、搬移依赖与完整校验的验收不访问原始 MinGW 安装目录。</summary>
static class BundledLvglChecks
{
    public static async Task<int> RunAsync(string sourceProject, string runtime, string? cliExecutable)
    {
        var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var evidence = Path.Combine(workspace, "artifacts", "validation", "lvgl-bundled-current");
        var relocatedCatalogRoot = Path.Combine(evidence, "relocated-toolsets");
        Directory.CreateDirectory(evidence);
        var checks = new List<string>();
        var environmentNames = new[] { "PATH", "GCC_EXEC_PREFIX", "COMPILER_PATH", "LIBRARY_PATH", "CPATH", "C_INCLUDE_PATH", "CPLUS_INCLUDE_PATH" };
        var previousEnvironment = environmentNames.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var builtinPath = Path.Combine(runtime, "toolsets", "pc.mingw", "1.0.0", "gcc", "bin", "gcc.exe");
        var data = Path.Combine(evidence, "fresh-machine-data");
        Directory.CreateDirectory(data);
        var settingsPath = Path.Combine(data, "lvgl-host-toolchain.json");
        if (File.Exists(settingsPath))
        {
            File.Delete(settingsPath);
        }
        var fixture = Path.Combine(evidence, "fixture");
        try
        {
            Environment.SetEnvironmentVariable("PATH", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32"));
            foreach (var name in environmentNames.Skip(1))
            {
                Environment.SetEnvironmentVariable(name, null);
            }
            Check(Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator).Length == 1,
                "validation PATH contains only Windows System32 and no external compiler path");
            await using var services = new WorkbenchService(runtime, data);
            Check(services.LvglPreview.UsingBundledToolchain && services.LvglPreview.ToolchainPath == builtinPath && !File.Exists(settingsPath),
                "fresh machine settings select bundled PC GCC without configuration or prompting");
            await CreateCppFixtureAsync(services, sourceProject, fixture);
            var authorizer = new BundledAuthorizer();
            await using var session = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services, fixture, authorizer));
            var launched = Parse(await session.CallToolAsync("lvgl_preview_start", "{}"));
            Check(launched.GetProperty("IsRunning").GetBoolean(), "actual MCP start builds and runs C plus C++ LVGL with the default bundled toolchain");
            Check(!File.Exists(settingsPath), "successful default preview does not create an external compiler preference");
            await Task.Delay(300);
            var frame = await session.CallToolDetailedAsync("lvgl_preview_screenshot", "{}");
            Check(frame.Images is [{ MimeType: "image/png" }], "bundled preview returns an actual PNG over MCP");
            await File.WriteAllBytesAsync(Path.Combine(evidence, "cpp-preview.png"), frame.Images[0].Data);
            using (var bitmap = SKBitmap.Decode(frame.Images[0].Data) ?? throw new InvalidDataException("Preview did not return a decodable PNG."))
            {
                var color = bitmap.GetPixel(118, 78);
                Check(color.Red is >= 15 and <= 20 && color.Green is >= 30 and <= 35 && color.Blue is >= 60 and <= 70,
                    "C++ STL-backed UI renders the expected framebuffer color");
            }
            _ = await session.CallToolAsync("lvgl_preview_input", "{\"type\":\"pointer\",\"x\":35,\"y\":25,\"pressed\":true}");
            _ = await session.CallToolAsync("lvgl_preview_input", "{\"type\":\"pointer\",\"x\":35,\"y\":25,\"pressed\":false}");
            await Task.Delay(100);
            var clicked = await session.CallToolDetailedAsync("lvgl_preview_screenshot", "{}");
            using (var bitmap = SKBitmap.Decode(clicked.Images.Single().Data) ?? throw new InvalidDataException("Clicked preview did not return a decodable PNG."))
            {
                Check(bitmap.GetPixel(118, 78).Red > 240, "bundled C++ UI receives real LVGL CLICKED input");
            }
            var stamp = Parse(await File.ReadAllTextAsync(Path.Combine(fixture, ".build", "pc-preview", "compiler.json")));
            Check(stamp.GetProperty("isBundled").GetBoolean() && stamp.GetProperty("toolsetId").GetString() == "pc.mingw" &&
                  Path.GetFullPath(stamp.GetProperty("gccPath").GetString()!) == builtinPath,
                "build receipt identifies pc.mingw and a compiler inside the active runtime");
            var buildLog = await File.ReadAllTextAsync(Path.Combine(fixture, ".build", "pc-preview", "build.log"));
            Check(!buildLog.Contains("E:/MCU/MINGW", StringComparison.OrdinalIgnoreCase) &&
                  !buildLog.Contains("E:\\MCU\\MINGW", StringComparison.OrdinalIgnoreCase),
                "build diagnostics contain no original external MinGW compiler path");
            File.Copy(Path.Combine(fixture, ".build", "pc-preview", "build.log"), Path.Combine(evidence, "builtin-build.log"), true);
            _ = await session.CallToolAsync("lvgl_preview_stop", "{}");
            Check(authorizer.Requests.All(request => request.Permission == StudioXMcpPermission.Build),
                "native preview requests only Build approval and no hardware operation");

            var legacyData = Path.Combine(evidence, "legacy-machine-data");
            Directory.CreateDirectory(legacyData);
            await File.WriteAllTextAsync(Path.Combine(legacyData, "lvgl-host-toolchain.json"),
                "{\"gccPath\":\"Z:/removed-developer-install/bin/gcc.exe\",\"target\":\"x86_64-w64-mingw32\",\"version\":\"13.1.0\",\"sha256\":\"legacy\"}");
            await using (var legacy = new WorkbenchService(runtime, legacyData))
            {
                Check(legacy.LvglPreview.UsingBundledToolchain && legacy.LvglPreview.ToolchainPath == builtinPath &&
                  legacy.LvglPreview.SettingsDiagnostic?.Contains("旧版", StringComparison.Ordinal) == true,
                "legacy raw external compiler preferences are disabled and migrate to the bundled default");
            }

            var missingRuntime = Path.Combine(evidence, "missing-runtime");
            await using (var missing = new WorkbenchService(missingRuntime, Path.Combine(evidence, "missing-machine-data")))
            {
                await using (var missingSession = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(missing, fixture, authorizer)))
                {
                    var unavailable = Parse(await missingSession.CallToolAsync("lvgl_preview_start", "{}"));
                    Check(!unavailable.GetProperty("IsRunning").GetBoolean() &&
                          unavailable.GetProperty("Log").GetString()!.Contains("TOOLSET_MISSING", StringComparison.Ordinal),
                        "missing native bundle fails explicitly without falling back to ambient or developer GCC");
                }
            }

            var catalog = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
            var original = await catalog.ResolveAsync("pc.mingw", "1.0.0", "mingw-gcc-13.1.0");
            Check(original.Manifest.Purpose == "windows-native" && !original.Manifest.Executables.ContainsKey("cmake") &&
                  !original.Manifest.Executables.ContainsKey("ninja"),
                "native toolset uses versioned windows-native roles and reuses existing CMake and Ninja");
            var relocatedTools = Path.Combine(relocatedCatalogRoot, "toolsets");
            var relocatedRoot = Path.Combine(relocatedTools, "pc.mingw", "1.0.0");
            CopyTree(original.RootDirectory, relocatedRoot, evidence);
            var relocatedCatalog = new ToolsetCatalog(relocatedTools);
            var moved = await relocatedCatalog.ResolveAsync("pc.mingw", "1.0.0", "mingw-gcc-13.1.0", forceVerification: true);
            Check(moved.Fingerprint == original.Fingerprint && moved.RootDirectory != original.RootDirectory,
                "physically copied native toolset verifies unchanged at a different absolute path");
            await CheckRelocatedCppAsync(moved, evidence);
            await services.LvglPreview.ConfigureToolchainAsync(moved.Tool("gcc"));
            Check(!services.LvglPreview.UsingBundledToolchain && services.LvglPreview.ToolchainPath == moved.Tool("gcc") &&
                  Parse(await File.ReadAllTextAsync(settingsPath)).GetProperty("mode").GetString() == "external",
                "explicit external override is opt-in and persists a separate external mode");
            await services.LvglPreview.UseBundledToolchainAsync();
            Check(services.LvglPreview.UsingBundledToolchain && services.LvglPreview.ToolchainPath == builtinPath &&
                  Parse(await File.ReadAllTextAsync(settingsPath)).GetProperty("mode").GetString() == "bundled",
                "reset returns to the versioned verified built-in compiler");

            var header = moved.Manifest.Sha256.Keys.First(path => path.Contains("/include/", StringComparison.Ordinal) && path.EndsWith(".h", StringComparison.Ordinal));
            var library = moved.Manifest.Sha256.Keys.First(path => path.EndsWith("libstdc++.a", StringComparison.Ordinal));
            foreach (var relative in new[] { moved.Manifest.Executables["gcc"], header, library })
            {
                await CheckTamperAsync(relocatedCatalog, moved.RootDirectory, relative);
            }
            var manifestPath = Path.Combine(moved.RootDirectory, "toolset.json");
            var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
            try
            {
                var manifest = JsonNode.Parse(manifestBytes)!;
                manifest["purpose"] = "unknown-validation-purpose";
                await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
                await ExpectCodeAsync(() => relocatedCatalog.ResolveAsync("pc.mingw", "1.0.0", "mingw-gcc-13.1.0", forceVerification: true),
                    "TOOLSET_PURPOSE", "unknown manifest purpose is rejected before using native tools");
            }
            finally { await File.WriteAllBytesAsync(manifestPath, manifestBytes); }
            _ = await relocatedCatalog.ResolveAsync("pc.mingw", "1.0.0", "mingw-gcc-13.1.0", forceVerification: true);

            if (cliExecutable is not null)
            {
                await using var external = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
                {
                    Command = Path.GetFullPath(cliExecutable),
                    Arguments = ["mcp", fixture, runtime],
                    Name = "StudioX bundled LVGL external MCP"
                }));
                var definitions = await external.ListToolsAsync();
                Check(definitions.Any(tool => tool.ProtocolTool.Name == "lvgl_preview_start") &&
                      definitions.Any(tool => tool.ProtocolTool.Name == "lvgl_resource_report"),
                    "external MCP exposes the same preview backend after native toolchain packaging");
            }
            DeleteOwnedCopy(relocatedCatalogRoot, evidence);
            Check(!Directory.Exists(relocatedCatalogRoot), "temporary independent native-toolchain copy is removed after verification");
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
            {
                success = true,
                hardware = false,
                bundled = true,
                checks,
                compiler = builtinPath,
                originalRuntime = runtime,
                toolsetFingerprint = original.Fingerprint,
                toolsetFiles = original.Manifest.Sha256.Count,
                externalPath = "not used",
                environmentPath = Environment.GetEnvironmentVariable("PATH"),
                temporaryCopyRetained = Directory.Exists(relocatedCatalogRoot)
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {checks.Count} bundled checks; evidence: {evidence}");
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
            {
                success = false,
                hardware = false,
                bundled = true,
                checks,
                error = ex.ToString(),
                temporaryCopyRetained = Directory.Exists(relocatedCatalogRoot)
            },
                new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            foreach (var pair in previousEnvironment)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
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
        async Task ExpectCodeAsync<T>(Func<Task<T>> operation, string code, string description)
        {
            try
            {
                _ = await operation();
                throw new InvalidOperationException("expected " + code);
            }
            catch (StudioXException ex) when (ex.Code == code) { Check(true, description); }
        }
        async Task CheckTamperAsync(ToolsetCatalog catalog, string root, string relative)
        {
            var file = Path.GetFullPath(Path.Combine(root, relative));
            var lastWrite = File.GetLastWriteTimeUtc(file);
            byte previous;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Position = stream.Length - 1;
                previous = (byte)stream.ReadByte();
                stream.Position--;
                stream.WriteByte((byte)(previous ^ 0x5a));
            }
            try
            {
                await ExpectCodeAsync(() => catalog.ResolveAsync("pc.mingw", "1.0.0", "mingw-gcc-13.1.0", forceVerification: true),
                    "TOOL_HASH", "full-tree verification rejects modified " + relative);
                await using var rejected = new WorkbenchService(relocatedCatalogRoot, Path.Combine(evidence, "tampered-machine-data"));
                await using var rejectedSession = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(rejected, fixture, new BundledAuthorizer()));
                var failed = Parse(await rejectedSession.CallToolAsync("lvgl_preview_start", "{}"));
                Check(!failed.GetProperty("IsRunning").GetBoolean() &&
                      failed.GetProperty("Log").GetString()!.Contains("[TOOL_HASH]", StringComparison.Ordinal),
                    "actual MCP start preserves TOOL_HASH and refuses a modified " + relative);
            }
            finally
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                stream.Position = stream.Length - 1;
                stream.WriteByte(previous);
                stream.Dispose();
                File.SetLastWriteTimeUtc(file, lastWrite);
            }
        }
        async Task CheckRelocatedCppAsync(ResolvedToolset tools, string output)
        {
            var source = Path.Combine(output, "relocation.cpp");
            var executable = Path.Combine(output, "relocation.exe");
            await File.WriteAllTextAsync(source, "#include <vector>\n#include <numeric>\n#include <iostream>\n#include <string>\nint main(){std::vector<int> v{1,2,3};std::cout << std::string(\"relocated STL \") << std::accumulate(v.begin(),v.end(),0);}\n");
            var runner = new ProcessRunner();
            var environment = new Dictionary<string, string> { ["PATH"] = Path.GetDirectoryName(tools.Tool("gcc"))! + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") };
            var compiled = await runner.RunAsync(new(tools.Tool("gxx"), ["-std=c++17", "-O2", "-static", "-static-libgcc", source, "-o", executable], output,
                TimeSpan.FromSeconds(45), environment, RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
            await File.WriteAllTextAsync(Path.Combine(output, "relocation-build.log"), compiled.StandardOutput + compiled.StandardError);
            Check(compiled.ExitCode == 0 && !compiled.TimedOut, "relocated g++ compiles real C++ STL with its own headers, libraries and linker");
            var executed = await runner.RunAsync(new(executable, [], output, TimeSpan.FromSeconds(10), environment));
            Check(executed.ExitCode == 0 && executed.StandardOutput == "relocated STL 6",
                "relocated statically linked C++ program runs with sanitized PATH");
        }
    }

    private static async Task CreateCppFixtureAsync(WorkbenchService services, string sourceProject, string fixture)
    {
        var original = await services.LvglPreview.ReadConfigurationAsync(sourceProject);
        var library = Path.GetFullPath(Path.Combine(sourceProject, original.LvglDirectory));
        Directory.CreateDirectory(Path.Combine(fixture, ".studiox"));
        Directory.CreateDirectory(Path.Combine(fixture, "src"));
        Directory.CreateDirectory(Path.Combine(fixture, "include"));
        File.Copy(Path.Combine(sourceProject, ".studiox", "project.json"), Path.Combine(fixture, ".studiox", "project.json"), true);
        await File.WriteAllTextAsync(Path.Combine(fixture, "include", "lv_conf_pc.h"), """
            #ifndef LV_CONF_H
            #define LV_CONF_H
            #define LV_COLOR_DEPTH 16
            #define LV_MEM_SIZE (36U * 1024U)
            #define LV_TICK_CUSTOM 1
            #define LV_TICK_CUSTOM_INCLUDE "lv_port_clock.h"
            #define LV_TICK_CUSTOM_SYS_TIME_EXPR (lv_port_millis())
            #define LV_USE_LOG 1
            #define LV_LOG_LEVEL LV_LOG_LEVEL_WARN
            #define LV_LOG_PRINTF 0
            #define LV_ASSERT_HANDLER_INCLUDE "lv_port.h"
            #define LV_ASSERT_HANDLER lv_port_panic();
            #define LV_DISP_DEF_REFR_PERIOD 5
            #define LV_INDEV_DEF_READ_PERIOD 5
            #define LV_THEME_DEFAULT_TRANSITION_TIME 0
            #endif
            """);
        await File.WriteAllTextAsync(Path.Combine(fixture, "src", "ui.cpp"), """
            #include "lvgl.h"
            #include <vector>
            #include <numeric>
            #include <string>
            #include <stdexcept>
            static void clicked(lv_event_t *event)
            {
                (void)event;
                std::vector<int> samples{10, 20, 30};
                if (std::accumulate(samples.begin(), samples.end(), 0) == 60)
                    lv_obj_set_style_bg_color(lv_scr_act(), lv_color_make(255, 0, 0), 0);
            }
            extern "C" void studiox_bundled_ui(void)
            {
                std::vector<int> samples{1, 2, 3};
                int sum = std::accumulate(samples.begin(), samples.end(), 0);
                if (sum != 6) throw std::runtime_error("STL initialization failed");
                std::string text = std::string("C++ STL ") + std::to_string(sum);
                lv_obj_set_style_bg_color(lv_scr_act(), lv_color_make(16, 32, 64), 0);
                lv_obj_set_style_bg_opa(lv_scr_act(), LV_OPA_COVER, 0);
                lv_obj_t *button = lv_btn_create(lv_scr_act());
                lv_obj_set_pos(button, 10, 10);
                lv_obj_set_size(button, 50, 30);
                lv_obj_add_event_cb(button, clicked, LV_EVENT_CLICKED, nullptr);
                lv_obj_t *label = lv_label_create(button);
                lv_label_set_text(label, text.c_str());
                lv_obj_center(label);
            }
            """);
        await services.LvglPreview.SaveConfigurationAsync(fixture, new(1, Path.GetRelativePath(fixture, library).Replace('\\', '/'),
            "include/lv_conf_pc.h", ["src/ui.cpp"], ["include"], "studiox_bundled_ui", Width: 120, Height: 80, Zoom: 1, AutoRebuild: false));
    }

    private static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement.Clone();
    private static void CopyTree(string source, string destination, string evidence)
    {
        ValidateOwnedCopy(destination, evidence);
        Directory.CreateDirectory(destination);
        foreach (var item in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if (item.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Validation cannot copy a toolchain reparse point.");
            }
            var target = Path.Combine(destination, item.Name);
            if (item is DirectoryInfo)
            {
                CopyTree(item.FullName, target, evidence);
            }
            else
            {
                File.Copy(item.FullName, target, true);
            }
        }
    }
    private static void DeleteOwnedCopy(string destination, string evidence)
    {
        ValidateOwnedCopy(destination, evidence);
        if (!Directory.Exists(destination))
        {
            return;
        }
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(destination));
        while (pending.TryPop(out var directory))
        {
            foreach (var item in directory.EnumerateFileSystemInfos())
            {
                ValidateOwnedCopy(item.FullName, evidence);
                if (item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new IOException("Validation copy cleanup encountered a reparse point.");
                }
                if (item is DirectoryInfo child)
                {
                    pending.Push(child);
                }
            }
        }
        Directory.Delete(destination, true);
    }
    private static void ValidateOwnedCopy(string destination, string evidence)
    {
        var allowed = Path.GetFullPath(Path.Combine(evidence, "relocated-toolsets"));
        var resolved = Path.GetFullPath(destination);
        if (!resolved.Equals(allowed, StringComparison.OrdinalIgnoreCase) && !resolved.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Validation copy target escaped its explicit temporary directory.");
        }
        if (Directory.Exists(resolved) && new DirectoryInfo(resolved).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("Validation copy target is a reparse point.");
        }
    }
    private sealed class BundledAuthorizer : IStudioXMcpAuthorizer
    {
        public List<StudioXMcpApprovalRequest> Requests { get; } = [];
        public Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(request.Permission == StudioXMcpPermission.Build);
        }
    }
}
