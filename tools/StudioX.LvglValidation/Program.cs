using System.Buffers.Binary;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using StudioX.Application;
using StudioX.Application.Lvgl;
using StudioX.Application.Mcp;
using StudioX.Engine.Lvgl;
using StudioX.Foundation;
using SkiaSharp;

if (args.Length is < 3 or > 5)
{
    Console.Error.WriteLine("usage: StudioX.LvglValidation <project> <runtime> <native-gcc> [studiox-cli] [--fixture-only]\n       StudioX.LvglValidation <project> <runtime> --bundled [studiox-cli]\n       StudioX.LvglValidation <project> <runtime> --custom-ui [studiox-cli]\n       StudioX.LvglValidation <project> <runtime> --legacy-resources");
    return 2;
}

var project = Path.GetFullPath(args[0]);
var runtime = Path.GetFullPath(args[1]);
if (args[2] == "--bundled")
{
    return await BundledLvglChecks.RunAsync(project, runtime, args.Length > 3 ? args[3] : null);
}
if (args[2] == "--custom-ui")
{
    return await CustomUiChecks.RunAsync(project, runtime, args.Length > 3 ? args[3] : null);
}
if (args[2] == "--legacy-resources")
{
    return await CustomUiChecks.RunLegacyResourcesAsync(project, runtime);
}
var compiler = Path.GetFullPath(args[2]);
var fixtureOnly = args.Skip(3).Contains("--fixture-only", StringComparer.Ordinal);
var evidence = Path.Combine(project, ".studiox", "validation", "pc-preview");
Directory.CreateDirectory(evidence);
var checks = new List<string>();
var previousMainChecks = Array.Empty<string>();
if (fixtureOnly)
{
    var previous = Path.Combine(evidence, "result-main.json");
    if (!File.Exists(previous))
    {
        previous = Path.Combine(evidence, "result.json");
    }
    if (File.Exists(previous))
    {
        var retained = Parse(await File.ReadAllTextAsync(previous));
        var items = retained.GetProperty("checks").EnumerateArray().Select(item => item.GetString()!).ToArray();
        var stoppedAt = Array.IndexOf(items, "stop cleans up only the owned preview process");
        if (stoppedAt >= 0)
        {
            previousMainChecks = items[..(stoppedAt + 1)];
            await File.WriteAllTextAsync(Path.Combine(evidence, "result-main.json"), JsonSerializer.Serialize(new
            {
                success = true,
                hardware = false,
                checks = previousMainChecks,
                note = "Retained successful main-project checks from the prior run; later fixture regression was fixed separately."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
var authorizer = new ValidationAuthorizer();
await using var services = new WorkbenchService(runtime, Path.Combine(evidence, "machine-data"));
await using var session = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services, project, authorizer));
var started = false;
try
{
    var transfer = LvglDisplayEstimate.Calculate(240, 320, 16, 30, 233025, "prior measured GPIO LCD throughput");
    Check(transfer.FullFrameBytes == 153600 && transfer.RequiredBytesPerSecond == 4608000 &&
          Math.Abs(transfer.FullFrameTransportLimitFps!.Value - 1.51708984375) < 0.000001,
        "display transport model uses actual pixel bytes and calibrated bandwidth without scaling PC FPS");
    Check(LvglDisplayEstimate.Calculate(240, 320, 16, 30).FullFrameTransportLimitFps is null,
        "missing calibrated bandwidth leaves target transfer limit unknown");
    foreach (var operation in new Action[] {
        () => LvglDisplayEstimate.Calculate(0, 320, 16, 30),
        () => LvglDisplayEstimate.Calculate(240, 320, 16, 0),
        () => LvglDisplayEstimate.Calculate(240, 320, 16, 30, 0) })
    {
        try
        {
            operation();
            throw new InvalidOperationException("invalid transfer model was accepted");
        }
        catch (StudioXException ex) when (ex.Code == "LVGL_TRANSFER_MODEL")
        {
            Check(true, "impossible display transport parameters are rejected");
        }
    }
    if (!fixtureOnly)
    {
        var names = (await session.ListToolsAsync()).Select(tool => tool.Name).ToArray();
        var expected = new[] { "lvgl_preview_start", "lvgl_preview_stop", "lvgl_preview_status",
        "lvgl_preview_screenshot", "lvgl_preview_input", "lvgl_resource_report" };
        Check(expected.All(names.Contains), "real MCP handshake advertises all six LVGL tools");
        var idle = Parse(await Call("lvgl_preview_status", new
        {
        }));
        Check(!idle.GetProperty("IsRunning").GetBoolean(), "status starts idle without compiling or starting a process");

        var denied = await Call("lvgl_preview_start", new
        {
        });
        Check(denied.Contains("MCP_APPROVAL_DENIED", StringComparison.Ordinal) &&
              authorizer.Requests is [{ Permission: StudioXMcpPermission.Build, Tool: "lvgl_preview_start" }],
            "start requires Build approval before compilation and native project execution");
        Check(!services.LvglPreview.GetSnapshot(project).IsRunning, "denied start leaves no preview process");
        authorizer.Allow = true;
        var before = authorizer.Requests.Count;
        await using (var unsaved = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services,
            project, authorizer, () => Task.FromResult(true))))
        {
            var blocked = await unsaved.CallToolAsync("lvgl_preview_start", "{}");
            Check(blocked.Contains("MCP_UNSAVED_FILES", StringComparison.Ordinal) &&
                  authorizer.Requests.Count == before,
                "unsaved editor state blocks start before requesting approval or executing code");
        }

        foreach (var input in new object[] {
        new { type = "global-key" }, new { type = "pointer", x = -1, y = 0 },
        new { type = "wheel", delta = 101 }, new { type = "key", key = 0 } })
        {
            var invalid = Parse(await Call("lvgl_preview_input", input));
            Check(HasCode(invalid, "LVGL_INPUT"), "malformed preview input returns actionable LVGL_INPUT");
        }

        await services.LvglPreview.ConfigureToolchainAsync(compiler);
        var result = Parse(await Call("lvgl_preview_start", new
        {
        }));
        started = result.GetProperty("IsRunning").GetBoolean();
        if (!started)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "start-failure.json"), result.GetRawText());
        }
        Check(started, "approved start compiles and launches an actual native LVGL window");
        var timeout = DateTime.UtcNow.AddSeconds(20);
        while (services.LvglPreview.GetSnapshot(project).Stats is not { Frames: > 0 } && DateTime.UtcNow < timeout)
        {
            await Task.Delay(200);
        }
        var status = Parse(await Call("lvgl_preview_status", new
        {
        }));
        Check(status.GetProperty("IsRunning").GetBoolean() &&
              status.GetProperty("Stats").GetProperty("Frames").GetInt64() > 0 &&
              status.GetProperty("PointerBits").GetInt32() is 32 or 64,
            "native process reports rendered frames and explicit host pointer width");
        await File.WriteAllTextAsync(Path.Combine(evidence, "status.json"), status.GetRawText());
        await using (var contender = new WorkbenchService(runtime, Path.Combine(evidence, "machine-data")))
        {
            try
            {
                await contender.LvglPreview.StartAsync(project);
                throw new InvalidOperationException("second Workbench acquired an already owned preview");
            }
            catch (StudioXException ex) when (ex.Code == "LVGL_PREVIEW_IN_USE")
            {
                Check(true, "second process/service cannot overwrite or control an owned project preview");
            }
        }

        var capture = await session.CallToolDetailedAsync("lvgl_preview_screenshot", "{}");
        var image = capture.Images.SingleOrDefault();
        Check(image?.MimeType == "image/png" && image.Data.AsSpan().StartsWith(new byte[]
            { 137, 80, 78, 71, 13, 10, 26, 10 }), "MCP carries a real PNG image content block");
        var imageBytes = image!.Data;
        var width = BinaryPrimitives.ReadInt32BigEndian(imageBytes.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(imageBytes.AsSpan(20, 4));
        var configuration = services.LvglPreview.GetSnapshot(project).Configuration!;
        Check(width == configuration.Width && height == configuration.Height,
            "PNG contains unscaled logical LVGL dimensions");
        await File.WriteAllBytesAsync(Path.Combine(evidence, "preview.png"), imageBytes);

        var offscreen = Parse(await Call("lvgl_preview_input", new
        {
            type = "pointer",
            x = width,
            y = height
        }));
        Check(HasCode(offscreen, "LVGL_INPUT"), "out-of-screen IPC coordinates are rejected");
        foreach (var input in new object[] {
        new { type = "pointer", x = width / 2, y = height / 2, pressed = true },
        new { type = "pointer", x = width / 2, y = height / 2, pressed = false },
        new { type = "wheel", delta = -1 }, new { type = "key", key = 10, pressed = true },
        new { type = "key", key = 10, pressed = false } })
        {
            var response = Parse(await Call("lvgl_preview_input", input));
            Check(response.GetProperty("IsRunning").GetBoolean(), "valid input is routed to the owned LVGL preview");
        }
        var resources = Parse(await Call("lvgl_resource_report", new
        {
        }));
        Check(resources.GetProperty("PcStats").ValueKind == JsonValueKind.Object &&
              resources.GetProperty("TargetBuild").ValueKind == JsonValueKind.Object &&
              resources.GetProperty("PcPointerBits").GetInt32() is 32 or 64 &&
              resources.GetProperty("Limitations").GetString() is { Length: > 20 },
            "resource report keeps target build and PC runtime metrics separate with limitations");
        var expectedDraw = (long)configuration.Width * configuration.DrawBufferRows *
            (configuration.ColorDepth / 8) * configuration.DrawBufferCount;
        Check(resources.GetProperty("DrawBufferBytes").GetInt64() == expectedDraw &&
              resources.GetProperty("PcFramebufferBytes").GetInt64() == (long)width * height * 4,
            "resource report distinguishes configured draw buffers from the extra PC display surface");
        Check(resources.GetProperty("DisplayTransfer").GetProperty("FullFrameBytes").GetInt64() == (long)width * height * 2,
            "MCP resource report includes a separately labeled display transport model");
        await File.WriteAllTextAsync(Path.Combine(evidence, "resources.json"), resources.GetRawText());

        if (args.Length >= 4 && args[3] != "--fixture-only")
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = Path.GetFullPath(args[3]),
                Arguments = ["mcp", project, runtime],
                Name = "StudioX LVGL external MCP validation"
            });
            await using var external = await McpClient.CreateAsync(transport);
            var definitions = await external.ListToolsAsync();
            Check(expected.All(name => definitions.Any(tool => tool.ProtocolTool.Name == name)),
                "external stdio client exposes the same six preview tools");
            var externalStatus = await external.CallToolAsync("lvgl_preview_status", new Dictionary<string, object?>());
            Check(externalStatus.IsError != true && externalStatus.Content.OfType<TextContentBlock>()
                .Any(item => item.Text.Contains("IsRunning", StringComparison.Ordinal)),
                "external stdio status uses the shared service without hardware access");
        }
        var stopped = Parse(await Call("lvgl_preview_stop", new
        {
        }));
        started = false;
        Check(!stopped.GetProperty("IsRunning").GetBoolean(), "stop cleans up only the owned preview process");
        await File.WriteAllTextAsync(Path.Combine(evidence, "result-main.json"), JsonSerializer.Serialize(new
        {
            success = true,
            hardware = false,
            checks = checks.ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    else
    {
        authorizer.Allow = true;
        await services.LvglPreview.ConfigureToolchainAsync(compiler);
    }
    await CheckInteractiveFixtureAsync();
    Check(authorizer.Requests.All(request => request.Permission == StudioXMcpPermission.Build),
        "validation requests no download, debug, serial or hardware permission");
    await File.WriteAllTextAsync(Path.Combine(evidence, fixtureOnly ? "result-fixture.json" : "result.json"), JsonSerializer.Serialize(new
    {
        success = true,
        hardware = false,
        simulated = true,
        checks,
        approvals = authorizer.Requests,
        evidence,
        note = "Real PC process/MCP/IPC validation; these metrics are not MCU measurements."
    }, new JsonSerializerOptions { WriteIndented = true }));
    if (fixtureOnly && previousMainChecks.Length > 0)
    {
        await File.WriteAllTextAsync(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new
        {
            success = true,
            hardware = false,
            simulated = true,
            mode = "main-project-and-fixture-across-runs",
            checks = previousMainChecks.Concat(checks.Skip(5)).ToArray(),
            mainEvidence = "result-main.json",
            fixtureEvidence = "result-fixture.json",
            note = "Main project MCP/IPC checks and repaired fixture checks passed; PC metrics are not MCU measurements."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    Console.WriteLine($"PASS {checks.Count} checks; evidence: {evidence}");
    return 0;
}
catch (Exception ex)
{
    await File.WriteAllTextAsync(Path.Combine(evidence, fixtureOnly ? "result-fixture.json" : "result.json"), JsonSerializer.Serialize(new
    {
        success = false,
        hardware = false,
        checks,
        error = ex.ToString()
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.Error.WriteLine(ex);
    return 1;
}
finally
{
    if (started)
    {
        await services.LvglPreview.StopAsync(project);
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
Task<string> Call(string tool, object input) => session.CallToolAsync(tool, JsonSerializer.Serialize(input));
static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement.Clone();
static bool HasCode(JsonElement value, string code) => value.TryGetProperty("code", out var field) && field.GetString() == code;

async Task CheckInteractiveFixtureAsync()
{
    var current = await services.LvglPreview.ReadConfigurationAsync(project);
    var library = Path.GetFullPath(Path.Combine(project, current.LvglDirectory));
    var fixture = Path.Combine(evidence, "fixture");
    Directory.CreateDirectory(Path.Combine(fixture, ".studiox"));
    Directory.CreateDirectory(Path.Combine(fixture, "src"));
    Directory.CreateDirectory(Path.Combine(fixture, "include"));
    File.Copy(Path.Combine(project, ".studiox", "project.json"), Path.Combine(fixture, ".studiox", "project.json"), true);
    await File.WriteAllTextAsync(Path.Combine(fixture, "include", "lv_conf_pc.h"), """
        #ifndef LV_CONF_H
        #define LV_CONF_H
        #define LV_COLOR_DEPTH 16
        #define LV_COLOR_16_SWAP 0
        #define LV_MEM_CUSTOM 0
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
        #define LV_USE_BTN 1
        #define LV_USE_LABEL 1
        #endif
        """);
    var sourcePath = Path.Combine(fixture, "src", "ui.c");
    const string source = """
        #include "lvgl.h"
        static unsigned clicks;
        static lv_obj_t *label;
        static void clicked(lv_event_t *event)
        {
            (void)event;
            ++clicks;
            lv_obj_set_style_bg_color(lv_scr_act(), clicks & 1 ? lv_color_make(0, 0, 255) : lv_color_make(0, 255, 0), 0);
            lv_label_set_text_fmt(label, "%u", clicks);
        }
        void studiox_validation_ui(void)
        {
            lv_obj_set_style_bg_color(lv_scr_act(), lv_color_black(), 0);
            lv_obj_set_style_bg_opa(lv_scr_act(), LV_OPA_COVER, 0);
            lv_obj_t *button = lv_btn_create(lv_scr_act());
            lv_obj_set_pos(button, 10, 10);
            lv_obj_set_size(button, 50, 30);
            lv_obj_add_event_cb(button, clicked, LV_EVENT_CLICKED, 0);
            label = lv_label_create(button);
            lv_label_set_text(label, "0");
            lv_obj_center(label);
            lv_group_t *group = lv_group_get_default();
            if (group) { lv_group_add_obj(group, button); lv_group_focus_obj(button); }
        }
        """;
    await File.WriteAllTextAsync(sourcePath, source);
    var fixtureConfiguration = new LvglPreviewConfiguration(1,
        Path.GetRelativePath(fixture, library).Replace('\\', '/'), "include/lv_conf_pc.h", ["src/ui.c"], ["include"],
        "studiox_validation_ui", Width: 120, Height: 80, DrawBufferRows: 10, Zoom: 1,
        AutoRebuild: false);
    await services.LvglPreview.SaveConfigurationAsync(fixture, fixtureConfiguration);
    await using var fixtureSession = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services, fixture, authorizer));
    try
    {
        var launched = Parse(await fixtureSession.CallToolAsync("lvgl_preview_start", "{}"));
        Check(launched.GetProperty("IsRunning").GetBoolean(), "independent button fixture runs through the same MCP backend");
        await Task.Delay(250);
        Check((await ReadCornerAsync()).Red < 10 && (await ReadCornerAsync()).Blue < 10,
            "initial fixture screenshot contains the expected black background");
        await FixtureInputAsync(new
        {
            type = "pointer",
            x = 35,
            y = 25,
            pressed = true
        });
        await FixtureInputAsync(new
        {
            type = "pointer",
            x = 35,
            y = 25,
            pressed = false
        });
        await Task.Delay(250);
        var clickedColor = await ReadCornerAsync();
        Check(clickedColor.Blue > 240 && clickedColor.Red < 10 && clickedColor.Green < 10,
            "back-to-back pointer press/release fires real LVGL CLICKED and changes rendered pixels");
        await FixtureInputAsync(new
        {
            type = "key",
            key = 10,
            pressed = true
        });
        await FixtureInputAsync(new
        {
            type = "key",
            key = 10,
            pressed = false
        });
        await Task.Delay(250);
        var keyboardColor = await ReadCornerAsync();
        Check(keyboardColor.Green > 240 && keyboardColor.Red < 10 && keyboardColor.Blue < 10,
            "LVGL ENTER through preview IPC activates the focused control");

        await services.LvglPreview.SaveConfigurationAsync(fixture, fixtureConfiguration with
        {
            Width = 160,
            Height = 100
        });
        await File.WriteAllTextAsync(sourcePath, "#error STUDIOX_INTENTIONAL_PREVIEW_BUILD_FAILURE\n");
        var failed = Parse(await fixtureSession.CallToolAsync("lvgl_preview_start", "{}"));
        Check(failed.GetProperty("IsRunning").GetBoolean() && failed.GetProperty("IsStale").GetBoolean() &&
              failed.GetProperty("Log").GetString()!.Contains("STUDIOX_INTENTIONAL_PREVIEW_BUILD_FAILURE", StringComparison.Ordinal),
            "failed rebuild preserves the last successful running window with a stale flag and original diagnostics");
        File.Copy(Path.Combine(fixture, ".build", "pc-preview", "build.log"), Path.Combine(evidence, "intentional-build-failure.log"), true);
        var oldFrame = await ReadCornerAsync();
        Check(oldFrame.Green > 240 && oldFrame.Blue < 10 &&
              services.LvglPreview.GetSnapshot(fixture).Configuration!.Width == 120,
            "failed config resize keeps old framebuffer dimensions and successful content");
        await File.WriteAllTextAsync(sourcePath, source);
        await services.LvglPreview.SaveConfigurationAsync(fixture, fixtureConfiguration with
        {
            AutoRebuild = true
        });
        var repaired = Parse(await fixtureSession.CallToolAsync("lvgl_preview_start", "{}"));
        Check(repaired.GetProperty("IsRunning").GetBoolean() && !repaired.GetProperty("IsStale").GetBoolean(),
            "corrected fixture source replaces the old window and clears the stale flag");

        var redSource = source.Replace("lv_color_black()", "lv_color_make(255, 0, 0)", StringComparison.Ordinal);
        await File.WriteAllTextAsync(sourcePath, redSource);
        var until = DateTime.UtcNow.AddSeconds(20);
        var updated = false;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(200);
            var observed = services.LvglPreview.GetSnapshot(fixture);
            if (observed is not { IsRunning: true, IsStale: false, State: "Running" })
            {
                continue;
            }
            var color = await ReadCornerAsync();
            if (color.Red > 240 && color.Green < 10 && color.Blue < 10)
            {
                updated = true;
                break;
            }
        }
        Check(updated, "saving shared UI source automatically rebuilds and updates actual window pixels");
        await File.WriteAllTextAsync(sourcePath, source);
        _ = await fixtureSession.CallToolAsync("lvgl_preview_stop", "{}");
        await Task.Delay(1000);
        Check(!services.LvglPreview.GetSnapshot(fixture).IsRunning,
            "manual stop cancels a pending save rebuild without resurrecting the preview");
    }
    finally
    {
        await File.WriteAllTextAsync(sourcePath, source);
        await services.LvglPreview.StopAsync(fixture);
    }

    async Task FixtureInputAsync(object input)
    {
        var response = Parse(await fixtureSession.CallToolAsync("lvgl_preview_input", JsonSerializer.Serialize(input)));
        if (response.TryGetProperty("error", out _))
        {
            throw new InvalidOperationException(response.GetRawText());
        }
    }
    async Task<SKColor> ReadCornerAsync()
    {
        var frame = await fixtureSession.CallToolDetailedAsync("lvgl_preview_screenshot", "{}");
        using var bitmap = SKBitmap.Decode(frame.Images.Single().Data);
        if (bitmap is null)
        {
            throw new InvalidDataException("Fixture PNG could not be decoded.");
        }
        return bitmap.GetPixel(118, 78);
    }
}

sealed class ValidationAuthorizer : IStudioXMcpAuthorizer
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
        return Task.FromResult(Allow);
    }
}
