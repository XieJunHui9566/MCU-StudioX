using System.Text.Json;
using System.Text.Json.Nodes;
using StudioX.Application;
using StudioX.Application.Mcp;

/// <summary>离线记录工具协议，用于确认职责拆分不改变客户端可见的契约。</summary>
internal static class ProviderContractChecks
{
    /// <summary>比较完整 JSON 契约，不能只验证工具名称或数量。</summary>
    public static async Task VerifyAsync(string baselinePath)
    {
        var currentPath = Path.GetTempFileName();
        try
        {
            await WriteAsync(currentPath);
            var baseline = JsonNode.Parse(await File.ReadAllTextAsync(baselinePath));
            var current = JsonNode.Parse(await File.ReadAllTextAsync(currentPath));
            if (!JsonNode.DeepEquals(baseline, current))
            {
                throw new InvalidOperationException("MCP 工具名称、参数 schema 或描述与基线不同。");
            }
            Console.WriteLine("PASS all MCP tool contracts match the recorded baseline.");
        }
        finally
        {
            File.Delete(currentPath);
        }
    }

    public static async Task WriteAsync(string outputPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "studiox-mcp-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var services = new WorkbenchService(Path.Combine(root, "runtime"), Path.Combine(root, "data"));
            await using var tools = new StudioXMcpTools(services, root, new DenyStudioXMcpAuthorizer());
            var contracts = tools.CreateToolCollection().Select(tool => tool.ProtocolTool).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(contracts,
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Recorded {contracts.Length} MCP tool contracts.");
        }
        finally
        {
            var full = Path.GetFullPath(root);
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (string.Equals(Path.GetDirectoryName(full), temporary, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(full).StartsWith("studiox-mcp-contract-", StringComparison.Ordinal))
            {
                Directory.Delete(full, recursive: true);
            }
        }
    }
}
