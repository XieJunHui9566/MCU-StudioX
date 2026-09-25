using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

internal static class DebugMemoryPagingChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        // 使用固定 F407 模拟传输；不启动 GDB/OpenOCD 进程，也不打开 USB 设备。
        var project = Path.Combine(root, "memory-paging-project");
        Directory.CreateDirectory(Path.Combine(project, ".studiox"));
        Directory.CreateDirectory(Path.Combine(project, "src"));
        await JsonStore.WriteAsync(Path.Combine(project, ".studiox", "project.json"),
            new ProjectManifest(1, "memory-paging", "demo.pack", "1.0.0", "offline",
                "STM32F407ZG", "blank", "gcc", "1.0.0", "gcc"));
        await File.WriteAllTextAsync(Path.Combine(project, F407DebugExample.RelativeFile),
            F407DebugExample.Source);

        await using var services = new WorkbenchService(Path.Combine(root, "memory-paging-runtime"),
            Path.Combine(root, "memory-paging-data"));
        await services.Debugger.OpenProjectAsync(project);
        await services.Debugger.StartOfflineAsync();
        await using var session = await StudioXMcpSession.CreateAsync(
            new StudioXMcpTools(services, project, new DenyStudioXMcpAuthorizer()));

        var defaultRead = await session.CallToolAsync("debug_read_memory",
            "{\"address\":\"0x20000000\"}");
        using (var result = JsonDocument.Parse(defaultRead))
            check(result.RootElement.GetProperty("byteCount").GetInt32() == 64 &&
                result.RootElement.GetProperty("requestedByteCount").GetInt32() == 64 &&
                result.RootElement.GetProperty("hex").GetString()!.Length == 128 &&
                result.RootElement.GetProperty("nextAddress").GetString() == "0x20000040",
                "debug_read_memory retains the 64-byte default and returns a continuation address");

        var page = await session.CallToolAsync("debug_read_memory",
            "{\"address\":\"0x20000000\",\"byteCount\":128}");
        using (var result = JsonDocument.Parse(page))
            check(result.RootElement.GetProperty("byteCount").GetInt32() == 128 &&
                result.RootElement.GetProperty("hex").GetString()!.Length == 256 &&
                result.RootElement.GetProperty("nextAddress").GetString() == "0x20000080" &&
                result.RootElement.GetProperty("complete").GetBoolean(),
                "debug_read_memory reads a 128-byte page without truncation");

        var finalPage = await session.CallToolAsync("debug_read_memory",
            "{\"address\":\"0x20000080\",\"byteCount\":128}");
        using (var result = JsonDocument.Parse(finalPage))
            check(result.RootElement.GetProperty("byteCount").GetInt32() == 128 &&
                result.RootElement.GetProperty("nextAddress").GetString() == "0x20000100",
                "debug_read_memory can continue to the end of the offline RAM window");

        var maximum = await session.CallToolAsync("debug_read_memory",
            "{\"address\":\"0x20000000\",\"byteCount\":256}");
        using (var result = JsonDocument.Parse(maximum))
            check(result.RootElement.GetProperty("byteCount").GetInt32() == 256 &&
                result.RootElement.GetProperty("hex").GetString()!.Length == 512,
                "debug_read_memory permits the adapter's verified 256-byte maximum");

        var oversized = await session.CallToolAsync("debug_read_memory",
            "{\"address\":\"0x20000000\",\"byteCount\":257}");
        check(oversized.Contains("error", StringComparison.OrdinalIgnoreCase),
            "debug_read_memory rejects a request above the transport bound");

        var overflow = await session.CallToolAsync("debug_read_memory",
            "{\"address\":\"0xFFFFFFFE\",\"byteCount\":3}");
        check(overflow.Contains("error", StringComparison.OrdinalIgnoreCase),
            "debug_read_memory rejects a range crossing the 32-bit address boundary");
    }
}
