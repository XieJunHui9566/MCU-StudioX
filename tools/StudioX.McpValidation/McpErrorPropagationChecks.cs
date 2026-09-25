using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Foundation;

internal static class McpErrorPropagationChecks
{
    public static async Task RunAsync(WorkbenchService services, string project,
        Action<bool, string> check)
    {
        var tools = new StudioXMcpTools(services, project, new DenyStudioXMcpAuthorizer());
        var wrapped = tools.CreateToolCollection();
        var original = typeof(StudioXMcpTools).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(method => McpServerTool.Create(method, tools))
            .OrderBy(tool => tool.ProtocolTool.Name, StringComparer.Ordinal).ToArray();
        check(wrapped.Count == original.Length &&
              wrapped.Zip(original).All(pair =>
                  JsonSerializer.Serialize(pair.First.ProtocolTool) == JsonSerializer.Serialize(pair.Second.ProtocolTool)),
            "MCP error adapter preserves every advertised tool schema and description");

        var adapterType = typeof(StudioXMcpTools).Assembly.GetType(
            "StudioX.Application.Mcp.StudioXMcpDiagnosticFunction", throwOnError: true)!;
        var sensitiveFunction = AIFunctionFactory.Create((Func<string>)(() =>
            throw new StudioXException("MCP_SAMPLE", "Authorization: Bearer abc123 https://user:pass@example.com/path")));
        var sensitiveAdapter = (AIFunction)Activator.CreateInstance(adapterType, sensitiveFunction)!;
        var sanitized = false;
        try { _ = await sensitiveAdapter.InvokeAsync(new AIFunctionArguments()); }
        catch (ModelContextProtocol.McpException error)
        {
            sanitized = error.Message.Contains("MCP_SAMPLE:", StringComparison.Ordinal) &&
                        !error.Message.Contains("abc123", StringComparison.Ordinal) &&
                        !error.Message.Contains("user:pass", StringComparison.Ordinal);
        }
        check(sanitized, "MCP business errors redact bearer tokens and URL credentials");

        var unexpectedFunction = AIFunctionFactory.Create((Func<string>)(() =>
            throw new InvalidOperationException("INTERNAL_SECRET_NEVER_EXPOSE")));
        var unexpectedAdapter = (AIFunction)Activator.CreateInstance(adapterType, unexpectedFunction)!;
        var remainedUnexpected = false;
        try { _ = await unexpectedAdapter.InvokeAsync(new AIFunctionArguments()); }
        catch (InvalidOperationException error)
        { remainedUnexpected = error.Message == "INTERNAL_SECRET_NEVER_EXPOSE"; }
        check(remainedUnexpected, "unknown exceptions remain outside the MCP business-error conversion");

        var badArgumentFunction = AIFunctionFactory.Create((Func<string>)(() =>
            throw new ArgumentException("byteCount 超出范围，password=abc123", "byteCount")));
        var badArgumentAdapter = (AIFunction)Activator.CreateInstance(adapterType, badArgumentFunction)!;
        var argumentSanitized = false;
        try { _ = await badArgumentAdapter.InvokeAsync(new AIFunctionArguments()); }
        catch (ModelContextProtocol.McpException error)
        {
            argumentSanitized = error.Message.Contains("MCP_ARGUMENT:", StringComparison.Ordinal) &&
                                error.Message.Contains("byteCount", StringComparison.Ordinal) &&
                                !error.Message.Contains("abc123", StringComparison.Ordinal);
        }
        check(argumentSanitized, "MCP argument diagnostics retain useful ranges while redacting secrets");

        await using var session = await StudioXMcpSession.CreateAsync(tools);
        var malformedArguments = await session.CallToolAsync("project_info", "[broken-json");
        check(HasCode(malformedArguments, "MCP_ARGUMENTS", "JSON"),
            "malformed model tool arguments receive a recoverable structured error");
        var range = await session.CallToolAsync("project_build_log", "{\"offsetBytes\":-1}");
        check(HasCode(range, "MCP_BUILD_LOG_RANGE", "偏移") &&
              !range.Contains("at StudioX", StringComparison.Ordinal) &&
              !range.Contains("System.", StringComparison.Ordinal),
            "built-in MCP returns a safe structured validation error rather than generic invocation text");
        var invalidMemoryCount = await session.CallToolAsync("debug_read_memory",
            "{\"address\":\"0x20000000\",\"byteCount\":257}");
        check(HasCode(invalidMemoryCount, "MCP_ARGUMENT", "1–256"),
            "debug argument range errors tell the model how to retry before hardware access");

        var source = await session.CallToolAsync("project_read_file", "{\"path\":\"src/main.c\"}");
        using var sourceJson = JsonDocument.Parse(source);
        var hash = sourceJson.RootElement.GetProperty("sha256").GetString();
        var impossiblePatch = await session.CallToolAsync("project_patch_file", JsonSerializer.Serialize(new
        {
            path = "src/main.c",
            originalSha256 = hash,
            hunks = new[] { new { oldText = "missing_unique_hunk_for_error_check", newText = "replacement" } }
        }));
        check(HasCode(impossiblePatch, "MCP_PATCH_MATCH", "唯一匹配"),
            "patch mismatch explains the recoverable cause before requesting write approval");

        var denied = await session.CallToolAsync("project_create_file",
            "{\"path\":\"src/mcp-denied.c\",\"content\":\"int denied;\\n\"}");
        check(HasCode(denied, "MCP_APPROVAL_DENIED", "授权") &&
              !File.Exists(Path.Combine(project, "src", "mcp-denied.c")),
            "approval denial remains visible and never mutates the project");
    }

    public static async Task RunExternalAsync(string cliExecutable, string project, string runtimeDirectory,
        Action<bool, string> check)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = cliExecutable,
            Arguments = ["mcp", project, runtimeDirectory],
            Name = "StudioX external MCP diagnostic validation"
        });
        await using var client = await McpClient.CreateAsync(transport);
        var result = await client.CallToolAsync("project_build_log",
            new Dictionary<string, object?> { ["offsetBytes"] = -1 });
        var content = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        check(result.IsError == true && content.Contains("MCP_BUILD_LOG_RANGE:", StringComparison.Ordinal) &&
              content.Contains("偏移", StringComparison.Ordinal) &&
              !content.Contains("System.Exception", StringComparison.Ordinal),
            "external stdio MCP exposes the same safe business error in isError content");
        var argumentResult = await client.CallToolAsync("debug_read_memory",
            new Dictionary<string, object?> { ["address"] = "0x20000000", ["byteCount"] = 257 });
        var argumentContent = string.Join("\n",
            argumentResult.Content.OfType<TextContentBlock>().Select(block => block.Text));
        check(argumentResult.IsError == true && argumentContent.Contains("MCP_ARGUMENT:", StringComparison.Ordinal) &&
              argumentContent.Contains("1–256", StringComparison.Ordinal),
            "external stdio MCP exposes actionable debug argument range errors");

        var pdfTools = await client.ListToolsAsync();
        check(new[] { "pdf_list", "pdf_inspect", "pdf_page" }.All(name =>
                pdfTools.Any(tool => tool.ProtocolTool.Name == name)),
            "external stdio MCP advertises all PDF reference tools");
        var pdfResult = await client.CallToolAsync("pdf_page", new Dictionary<string, object?>
        {
            ["scope"] = "project", ["path"] = "docs/board.pdf", ["page"] = 2,
            ["includeImage"] = true, ["maxDimension"] = 600
        });
        var pdfImage = pdfResult.Content.OfType<ImageContentBlock>().SingleOrDefault();
        check(pdfResult.IsError != true && pdfImage?.MimeType == "image/png" &&
              pdfImage.DecodedData.Span.StartsWith(new byte[] { 137, 80, 78, 71 }) &&
              pdfResult.Content.OfType<TextContentBlock>().Any(block =>
                  block.Text.Contains("\"page\":2", StringComparison.Ordinal)),
            "external stdio MCP carries both PDF page metadata and a rendered PNG block");
    }

    private static bool HasCode(string response, string code, string messagePart)
    {
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        return root.TryGetProperty("error", out var error) &&
               error.GetString()?.Contains(code + ":", StringComparison.Ordinal) == true &&
               root.TryGetProperty("code", out var actualCode) && actualCode.GetString() == code &&
               root.TryGetProperty("message", out var message) &&
               message.GetString()?.Contains(messagePart, StringComparison.Ordinal) == true;
    }
}
