using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

/// <summary>真实 MCP 握手与离线暂停会话验证；不创建硬件传输或调用 OpenOCD。</summary>
internal static class DisassemblyMcpChecks
{
    public static async Task RunAsync()
    {
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var root = Path.GetFullPath(Path.Combine(tempRoot, "studiox-disassembly-mcp-" + Guid.NewGuid().ToString("N")));
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
                    new ProjectManifest(1, "disassembly-test", "demo.pack", "1.0.0", "offline",
                        "STM32F407ZG", "demo", "gcc", "1.0.0", "gcc"));
                await File.WriteAllTextAsync(Path.Combine(directory, "src", "main.c"), F407DebugExample.Source);
            }
            var authorizer = new SwitchingAuthorizer();
            await using var services = new WorkbenchService(Path.Combine(root, "runtime"), Path.Combine(root, "data"));
            await using var session = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services, project, authorizer));
            var definition = (await session.ListToolsAsync()).Single(item => item.Name == "debug_disassemble");
            var schema = Parse(definition.ParametersJson);
            Check(schema.GetProperty("properties").TryGetProperty("address", out _) &&
                schema.GetProperty("properties").TryGetProperty("byteCount", out _) &&
                (!schema.TryGetProperty("required", out var required) ||
                    !required.EnumerateArray().Any(value => value.GetString() is "address" or "byteCount")),
                "real MCP handshake exposes optional disassembly address and byte count");
            Check(Error(Parse(await session.CallToolAsync("debug_disassemble", "{}")), "DEBUG_PROJECT"),
                "unbound MCP debugger cannot read disassembly");
            await services.Debugger.OpenProjectAsync(project);
            Check(Error(Parse(await session.CallToolAsync("debug_disassemble", "{}")), "DEBUG_STATE"),
                "disconnected debug session cannot read disassembly");
            await services.Debugger.StartOfflineAsync();

            var current = Parse(await session.CallToolAsync("debug_disassemble", "{}"));
            var pc = services.Debugger.Snapshot.Registers.Single(item => item.Name == "pc").Value;
            var rows = current.GetProperty("instructions").EnumerateArray().ToArray();
            Check(current.GetProperty("startAddress").GetString()!.Equals(pc, StringComparison.OrdinalIgnoreCase) &&
                current.GetProperty("programCounter").GetString()!.Equals(pc, StringComparison.OrdinalIgnoreCase) &&
                current.GetProperty("requestedByteCount").GetInt32() == 128 && rows.Length > 0 &&
                rows.Count(row => row.GetProperty("isProgramCounter").GetBoolean()) == 1,
                "omitted MCP address reads current executing PC and marks exactly one instruction");
            Check(current.GetProperty("simulated").GetBoolean() && !current.GetProperty("hardware").GetBoolean() &&
                current.GetProperty("evidence").GetString()!.Contains("模拟", StringComparison.Ordinal) &&
                rows.All(row => row.GetProperty("instruction").GetString()!.Contains("模拟", StringComparison.Ordinal)) &&
                rows.All(row => row.GetProperty("opcodes").GetString()!.Length > 0 &&
                    row.GetProperty("symbol").GetString()!.Length > 0),
                "MCP disassembly includes machine bytes and symbols with explicit simulation evidence");
            var manual = Parse(await session.CallToolAsync("debug_disassemble", "{\"address\":\"08000100\",\"byteCount\":16}"));
            Check(manual.GetProperty("startAddress").GetString() == "0x08000100" &&
                manual.GetProperty("endAddress").GetString() == "0x08000110" &&
                manual.GetProperty("requestedByteCount").GetInt32() == 16 &&
                !manual.GetProperty("instructions").EnumerateArray().Any(row => row.GetProperty("isProgramCounter").GetBoolean()),
                "explicit MCP hexadecimal range keeps the real PC separate from the requested location");
            foreach (var count in new[] { -1, 0, 513 })
                Check(Error(Parse(await session.CallToolAsync("debug_disassemble", JsonSerializer.Serialize(new { byteCount = count }))), "MCP_ARGUMENT"),
                    $"MCP rejects invalid disassembly byte count {count}");
            foreach (var address in new[] { "", "invalid", "0x100000000", "0xFFFFFFFF" })
                Check(Error(Parse(await session.CallToolAsync("debug_disassemble", JsonSerializer.Serialize(new { address, byteCount = 1 }))), "MCP_ARGUMENT"),
                    $"MCP rejects malformed or overflowing disassembly address '{address}'");
            var rawError = Parse(await session.CallToolAsync("debug_disassemble", "{\"address\":\"0x20000000\",\"byteCount\":16}"));
            Check(Error(rawError, "GDB_COMMAND") && rawError.GetProperty("message").GetString()!.Contains("离线示例仅提供", StringComparison.Ordinal) &&
                services.Debugger.State == DebugState.Stopped,
                "MCP preserves original GDB refusal and leaves paused session usable");

            async Task StepAsync(DebugAction action)
            {
                await services.Debugger.ExecuteAsync(action);
                var waited = Parse(await session.CallToolAsync("debug_wait", "{\"timeoutMs\":2000}"));
                Check(waited.GetProperty("state").GetString() == "Stopped" && !waited.GetProperty("timedOut").GetBoolean(),
                    "offline step reaches a paused frame through MCP wait");
            }
            await StepAsync(DebugAction.StepOver);
            await StepAsync(DebugAction.StepOver);
            await StepAsync(DebugAction.StepOver);
            await StepAsync(DebugAction.StepInto);
            await services.Debugger.RefreshAsync(1);
            var caller = services.Debugger.Snapshot.Frames.Single(frame => frame.Level == 1).Address;
            var selectedCaller = Parse(await session.CallToolAsync("debug_disassemble", "{\"byteCount\":16}"));
            var executing = services.Debugger.Snapshot.Frames.Single(frame => frame.Level == 0).Address;
            Check(!caller.Equals(executing, StringComparison.OrdinalIgnoreCase) &&
                selectedCaller.GetProperty("startAddress").GetString()!.Equals(executing, StringComparison.OrdinalIgnoreCase),
                "MCP default disassembly follows executing PC while caller frame is selected");
            await services.Debugger.ExecuteAsync(DebugAction.Continue);
            Check(Error(Parse(await session.CallToolAsync("debug_disassemble", "{}")), "DEBUG_STATE"),
                "running debug target refuses MCP disassembly reads");
            await services.Debugger.StopAsync();
            Check(Error(Parse(await session.CallToolAsync("debug_disassemble", "{}")), "DEBUG_STATE"),
                "stopped session cannot reuse old MCP disassembly");
            await services.Debugger.OpenProjectAsync(otherProject);
            Check(Error(Parse(await session.CallToolAsync("debug_disassemble", "{}")), "DEBUG_PROJECT"),
                "MCP cannot read a different IDE project's disassembly");
            Check(authorizer.Requests.Count == 0, "all disassembly reads and rejections require no write authorization");
            Console.WriteLine($"PASS {checks} disassembly MCP checks; offline simulation only.");
        }
        finally
        {
            // 删除范围必须保持在本次新建的临时目录，不能沿用工程、用户数据或工具目录。
            if (root.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(root).StartsWith("studiox-disassembly-mcp-", StringComparison.Ordinal) && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
