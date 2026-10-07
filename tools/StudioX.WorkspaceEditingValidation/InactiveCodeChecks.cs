using System.Text.Json;
using StudioX.Application.CodeIntelligence;
using StudioX.Foundation;

internal static class InactiveCodeChecks
{
    public static async Task RunAsync(string runtime, string fixture, string output, Action<bool, string> check)
    {
        Directory.CreateDirectory(output);
        var project = Path.Combine(output, "project");
        foreach (var relative in new[] { ".studiox/project.json", "device/manifest.json" })
        {
            var target = Path.Combine(project, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(fixture, relative), target);
        }
        Directory.CreateDirectory(Path.Combine(project, "src"));
        Directory.CreateDirectory(Path.Combine(project, "include"));
        Directory.CreateDirectory(Path.Combine(project, ".build"));
        await File.WriteAllTextAsync(Path.Combine(project, ".studiox/keil-import.json"), "{}");
        const string path = "src/inactive.c";
        const string other = "src/other.c";
        const string headerPath = "include/features.h";
        const string header = "#define LOCAL_FEATURE 2\r\n";
        var source = string.Join("\r\n", new[]
        {
            "// 中文 😀；注释里的 #if 0 不控制代码。", "#include \"features.h\"", "",
            "void Board_Delay(unsigned value);", "void vTaskDelay(unsigned value);", "unsigned pdMS_TO_TICKS(unsigned value);", "",
            "void System_Delay(unsigned milliseconds)", "{", "#ifdef STUDIOX_USE_FREERTOS",
            "    vTaskDelay(pdMS_TO_TICKS(milliseconds));", "#else", "    Board_Delay(milliseconds);", "#endif", "}", "",
            "#if 0", "int never_compiled = UNDEFINED_IN_DEAD_BRANCH;", "#elif defined(TARGET_FEATURE) && TARGET_FEATURE == 7",
            "int target_seven = 7;", "#else", "int target_other = 1;", "#endif", "",
            "#if LOCAL_FEATURE == 2", "int local_two = 2;", "#if defined(TARGET_FEATURE)", "int nested_defined = 1;",
            "#else", "int nested_missing = 0;", "#endif", "#else", "int local_other = 3;", "#endif", "",
            "const char *directive_literal = \"#if 0\";", "int ordinary_active = 1;", ""
        });
        await File.WriteAllTextAsync(Path.Combine(project, path), source);
        await File.WriteAllTextAsync(Path.Combine(project, other), source);
        await File.WriteAllTextAsync(Path.Combine(project, headerPath), header);
        var responsePath = Path.Combine(project, ".build/target.rsp");
        const string flags = "-mcpu=cortex-m3 -mthumb -DTARGET_FEATURE=7 -I../include -std=gnu11";
        await File.WriteAllTextAsync(responsePath, flags);
        await JsonStore.WriteAsync(Path.Combine(project, ".build/compile_commands.json"), new[]
        {
            new { directory = Path.Combine(project, ".build"), file = "../" + path, command = "arm-none-eabi-gcc @target.rsp -c ../" + path },
            new { directory = Path.Combine(project, ".build"), file = "../" + other, command = "arm-none-eabi-gcc -mcpu=cortex-m3 -mthumb -DTARGET_FEATURE=1 -I../include -c ../" + other }
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await using var service = new CodeIntelligenceService(runtime, Path.Combine(output, "language"));
        await service.StartAsync(project, token);
        async Task<CodeInactiveRegionBatch> Analyze(string dependency, string text)
        {
            await service.SynchronizeDiagnosticsAsync(path, text, [new(path, text), new(other, source), new(headerPath, dependency)], token);
            return service.GetInactiveRegions().SingleOrDefault(batch => batch.Path == path && batch.Text == text)
                ?? throw new InvalidOperationException("No inactive regions for current source: " + string.Join('\n', service.DrainLog()));
        }
        static bool Muted(CodeInactiveRegionBatch batch, string marker) => batch.Regions.Any(range =>
        {
            var start = CodePositions.ToOffset(batch.Text, range.Start);
            var end = CodePositions.ToOffset(batch.Text, range.End);
            return batch.Text[start..end].Contains(marker, StringComparison.Ordinal);
        });
        var initial = await Analyze(header, source);
        check(Muted(initial, "vTaskDelay(pdMS_TO_TICKS") && !Muted(initial, "Board_Delay(milliseconds)"),
            "actual CMake target without FreeRTOS dims the RTOS branch and retains the bare-metal branch");
        check(Muted(initial, "never_compiled") && Muted(initial, "target_other") && !Muted(initial, "target_seven"),
            "clangd handles #if 0, defined(), expressions, #elif and #else using target defines");
        check(Muted(initial, "local_other") && Muted(initial, "nested_missing") && !Muted(initial, "nested_defined") && !Muted(initial, "local_two"),
            "included header definitions and nested preprocessor branches determine inactive ranges");
        check(!Muted(initial, "directive_literal") && !Muted(initial, "ordinary_active") && !Muted(initial, "中文"),
            "directive-looking text in comments and strings cannot dim ordinary active code");
        var otherBatch = service.GetInactiveRegions().Single(batch => batch.Path == other);
        check(Muted(otherBatch, "target_seven") && !Muted(otherBatch, "target_other"), "each source uses its own real CMake compilation command");
        service.InvalidateDiagnostics();
        check(service.GetInactiveRegions().Count == 0, "editing any source or dependency immediately removes all stale inactive ranges");
        var changedHeader = header.Replace("2", "3", StringComparison.Ordinal);
        var dependencyChanged = await Analyze(changedHeader, source);
        check(Muted(dependencyChanged, "local_two") && !Muted(dependencyChanged, "local_other"),
            "unsaved header macro change refreshes an unchanged source's inactive branch");
        await File.WriteAllTextAsync(responsePath, flags + " -DSTUDIOX_USE_FREERTOS", token);
        await service.RefreshEnvironmentAsync(token);
        check(service.GetInactiveRegions().Count == 0, "target response-file change revokes the previous compile configuration's ranges");
        var rtos = await Analyze(header, source);
        check(!Muted(rtos, "vTaskDelay(pdMS_TO_TICKS") && Muted(rtos, "Board_Delay(milliseconds)"),
            "enabling FreeRTOS in the CMake target reverses the muted branch");
        var stale = service.SynchronizeDiagnosticsAsync(path, source, [new(path, source), new(headerPath, header)], token);
        service.InvalidateDiagnostics();
        var edited = "// new revision\r\n" + source;
        await Analyze(header, edited);
        await stale;
        check(service.GetInactiveRegions().Where(batch => batch.Path == path).All(batch => batch.Text == edited),
            "late requests never apply inactive regions to a different text revision");
        service.SetDiagnosticsSuspended(true);
        check(service.GetInactiveRegions().Count == 0, "debug suspension clears inactive analysis rather than retaining stale colors");
        service.SetDiagnosticsSuspended(false);
        check(service.GetInactiveRegions().Count == 0, "resume waits for fresh branch analysis");
        await service.StopAsync();
        check(service.GetInactiveRegions().Count == 0, "closing a project clears inactive-code state");

        IReadOnlyList<CodeRange> Decode(string json, string text = "😀x\r\ny\r\n")
        {
            using var document = JsonDocument.Parse(json);
            return InactiveCodeTokens.Decode(document.RootElement, text, 1, 2);
        }
        check(Decode("{\"data\":[0,2,1,1,0,1,0,1,1,0]}").Select(range => range.Start)
            .SequenceEqual(new CodePosition[] { new(0, 2), new(1, 0) }), "semantic token decoder preserves UTF-16 supplementary characters and CRLF coordinates");
        check(Decode("{\"data\":[0,0,4,1,0,1,0,2,1,0]}").Select(range => range.End)
            .SequenceEqual(new CodePosition[] { new(0, 3), new(1, 1) }), "clangd's whole-line inactive markers exclude CRLF terminators from the displayed span");
        check(Decode("{\"data\":[1,0,0,1,0]}", "a\n\nz").Count == 0, "empty inactive LF line does not create a zero-width color span");
        foreach (var invalid in new[]
        {
            "{\"data\":[0,0,1]}", "{\"data\":[5,0,1,1,0]}", "{\"data\":[0,4,1,1,0]}",
            "{\"data\":[0,-1,1,1,0]}", "{\"data\":[0,0,1,8,0]}", "{\"data\":[0,0,3,1,0,0,1,1,1,0]}"
        })
        {
            try
            {
                Decode(invalid);
                throw new InvalidOperationException("Malformed inactive token accepted.");
            }
            catch (JsonException) { }
        }
        check(true, "malformed, out-of-bounds, negative, unknown and overlapping token ranges are rejected without guessing");
        // UI 验证使用已启用 RTOS 的原始磁盘文本；未保存的检查只存在于前述语言会话。
        await JsonStore.WriteAsync(Path.Combine(output, "initial-ranges.json"), initial);
        await JsonStore.WriteAsync(Path.Combine(output, "rtos-ranges.json"), rtos);
        await File.WriteAllLinesAsync(Path.Combine(output, "clangd.log"), service.DrainLog(), token);
    }
}
