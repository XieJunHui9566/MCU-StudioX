using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

/// <summary>通过真实 MCP 握手验证 RTOS 只读入口；裸机模拟不得伪造出 FreeRTOS 数据。</summary>
internal static class FreeRtosMcpChecks
{
    public static async Task RunAsync()
    {
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var root = Path.GetFullPath(Path.Combine(tempRoot, "studiox-rtos-mcp-" + Guid.NewGuid().ToString("N")));
        var checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new Exception(description);
            Console.WriteLine("PASS " + description);
            checks++;
        }
        static JsonElement Parse(string result) => JsonSerializer.Deserialize<JsonElement>(result);
        static bool Error(JsonElement result, string code) =>
            result.TryGetProperty("code", out var value) && value.GetString() == code;

        Directory.CreateDirectory(root);
        try
        {
            var project = Path.Combine(root, "project");
            var otherProject = Path.Combine(root, "other-project");
            foreach (var directory in new[] { project, otherProject })
            {
                Directory.CreateDirectory(Path.Combine(directory, "src"));
                await JsonStore.WriteAsync(Path.Combine(directory, ".studiox", "project.json"),
                    new ProjectManifest(1, "rtos-mcp-test", "demo.pack", "1.0.0", "offline",
                        "STM32F407ZG", "demo", "gcc", "1.0.0", "gcc"));
                await File.WriteAllTextAsync(Path.Combine(directory, "src", "main.c"), F407DebugExample.Source);
            }
            var authorizer = new SwitchingAuthorizer();
            await using var services = new WorkbenchService(Path.Combine(root, "runtime"), Path.Combine(root, "data"));
            await using var session = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services, project, authorizer));
            var definition = (await session.ListToolsAsync()).Single(item => item.Name == "debug_rtos_snapshot");
            var schema = Parse(definition.ParametersJson);
            Check(schema.GetProperty("properties").TryGetProperty("objectSymbols", out _) &&
                (!schema.TryGetProperty("required", out var required) ||
                    !required.EnumerateArray().Any(value => value.GetString() == "objectSymbols")),
                "real MCP handshake exposes optional RTOS object symbols");
            Check(Error(Parse(await session.CallToolAsync("debug_rtos_snapshot", "{}")), "DEBUG_PROJECT"),
                "unbound MCP debugger cannot read RTOS state");
            await services.Debugger.OpenProjectAsync(project);
            Check(Error(Parse(await session.CallToolAsync("debug_rtos_snapshot", "{}")), "DEBUG_STATE"),
                "disconnected debug session cannot read RTOS state");
            await services.Debugger.StartOfflineAsync();
            var pausedSnapshot = services.Debugger.Snapshot;
            var current = Parse(await session.CallToolAsync("debug_rtos_snapshot", "{}"));
            if (!current.TryGetProperty("snapshot", out _))
                throw new Exception("Paused RTOS MCP read has no snapshot: " + current.GetRawText());
            Check(true, "paused RTOS MCP read returns a structured snapshot");
            var rtos = current.GetProperty("snapshot");
            Check(current.GetProperty("simulated").GetBoolean() && !current.GetProperty("hardware").GetBoolean() &&
                current.GetProperty("evidence").GetString()!.Contains("离线", StringComparison.Ordinal),
                "RTOS MCP response distinguishes offline data from hardware evidence");
            Check(!rtos.GetProperty("isAvailable").GetBoolean() &&
                rtos.GetProperty("tasks").GetArrayLength() == 0 &&
                rtos.GetProperty("objects").GetArrayLength() == 0 &&
                rtos.GetProperty("heap").ValueKind == JsonValueKind.Null &&
                rtos.GetProperty("diagnostics").GetArrayLength() > 0,
                "bare-metal F407 simulation returns unavailable RTOS with diagnostics instead of fabricated tasks");
            Check(ReferenceEquals(pausedSnapshot, services.Debugger.Snapshot) && services.Debugger.State == DebugState.Stopped,
                "RTOS MCP read preserves paused source context and debug snapshot");

            foreach (var symbol in new[] { "vTaskDelay(1)", "*queue", "queue[0]", "queue;continue", "" })
            {
                var invalid = Parse(await session.CallToolAsync("debug_rtos_snapshot",
                    JsonSerializer.Serialize(new { objectSymbols = new[] { symbol } })));
                Check(invalid.TryGetProperty("error", out _) && services.Debugger.State == DebugState.Stopped,
                    $"RTOS MCP refuses executable or unsupported object expression '{symbol}'");
            }
            await services.Debugger.ExecuteAsync(DebugAction.Continue);
            Check(Error(Parse(await session.CallToolAsync("debug_rtos_snapshot", "{}")), "DEBUG_STATE"),
                "running debug target refuses RTOS MCP reads");
            await services.Debugger.StopAsync();
            Check(Error(Parse(await session.CallToolAsync("debug_rtos_snapshot", "{}")), "DEBUG_STATE"),
                "ended debug session cannot reuse an old RTOS response");
            await services.Debugger.OpenProjectAsync(otherProject);
            Check(Error(Parse(await session.CallToolAsync("debug_rtos_snapshot", "{}")), "DEBUG_PROJECT"),
                "MCP cannot read another IDE project's RTOS state");
            Check(authorizer.Requests.Count == 0, "RTOS reads and rejections request no write authorization");
            Console.WriteLine($"PASS {checks} FreeRTOS MCP checks; offline only.");
        }
        finally
        {
            // 只回收本次创建的临时目录，避免测试删除工程或正常用户数据。
            if (root.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(root).StartsWith("studiox-rtos-mcp-", StringComparison.Ordinal) && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
