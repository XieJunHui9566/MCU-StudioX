namespace StudioX.Application.Mcp;

using System.IO.Pipelines;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using StudioX.Application.Skills;

/// <summary>工具图像只在当前模型请求中使用，不能写进对话历史或工具文本。</summary>
public sealed record StudioXMcpImage(string MimeType, byte[] Data);

public sealed record StudioXMcpToolResult(string Text, IReadOnlyList<StudioXMcpImage> Images);

/// <summary>内置 Agent 通过真实 MCP 客户端发现并调用同进程的 MCP 服务端。</summary>
public sealed class StudioXMcpSession : IAsyncDisposable
{
    private readonly McpServer server;
    private readonly McpClient client;
    private readonly StudioXMcpTools tools;
    private readonly Task serverLoop;

    private StudioXMcpSession(McpServer server, McpClient client, StudioXMcpTools tools, Task serverLoop)
    {
        this.server = server;
        this.client = client;
        this.tools = tools;
        this.serverLoop = serverLoop;
    }

    public string Project => tools.Project;

    /// <summary>内置 Agent 的提示目录与 MCP 工具使用同一工程技能设置。</summary>
    public AgentSkillDiscovery DiscoverSkills() => tools.Services.AiSkills.Discover(Project);

    public static async Task<StudioXMcpSession> CreateAsync(StudioXMcpTools tools,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(tools);
        Pipe clientToServer = new();
        Pipe serverToClient = new();
        var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            new McpServerOptions { ToolCollection = [.. tools.CreateToolCollection()] });
        var serverLoop = server.RunAsync();
        try
        {
            var client = await McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
                cancellationToken: token).ConfigureAwait(false);
            return new StudioXMcpSession(server, client, tools, serverLoop);
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            await tools.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<AiToolDefinition>> ListToolsAsync(CancellationToken token = default)
    {
        var listed = await client.ListToolsAsync(cancellationToken: token).ConfigureAwait(false);
        return listed.OrderBy(item => item.ProtocolTool.Name, StringComparer.Ordinal)
            .Select(item => new AiToolDefinition(item.ProtocolTool.Name,
            item.ProtocolTool.Description ?? "", item.ProtocolTool.InputSchema.GetRawText())).ToArray();
    }

    public async Task<string> CallToolAsync(string name, string argumentsJson,
        CancellationToken token = default) =>
        (await CallToolDetailedAsync(name, argumentsJson, token).ConfigureAwait(false)).Text;

    public async Task<StudioXMcpToolResult> CallToolDetailedAsync(string name, string argumentsJson,
        CancellationToken token = default)
    {
        Dictionary<string, object?>? arguments;
        try { arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson); }
        catch (JsonException)
        { return new(JsonSerializer.Serialize(new { error = "MCP_ARGUMENTS: 工具参数必须是 JSON 对象。",
            code = "MCP_ARGUMENTS", message = "工具参数必须是 JSON 对象。" }), []); }
        if (arguments is null)
            return new(JsonSerializer.Serialize(new { error = "MCP_ARGUMENTS: 工具参数必须是 JSON 对象。",
                code = "MCP_ARGUMENTS", message = "工具参数必须是 JSON 对象。" }), []);
        var result = await client.CallToolAsync(name, arguments, cancellationToken: token)
            .ConfigureAwait(false);
        var content = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(item => item.Text));
        if (result.IsError != true)
        {
            var images = result.Content.OfType<ImageContentBlock>()
                .Where(image => image.MimeType is "image/png" or "image/jpeg")
                .Select(image => new StudioXMcpImage(image.MimeType, image.DecodedData.ToArray()))
                .ToArray();
            return new(content, images);
        }
        var match = Regex.Match(content,
            "^(?:An error occurred invoking '[^'\\r\\n]{1,100}': )?(?<code>[A-Z][A-Z0-9_]{1,63}): (?<message>[^\\r\\n]{1,600})$",
            RegexOptions.CultureInvariant);
        return match.Success
            ? new(JsonSerializer.Serialize(new
            {
                error = match.Groups["code"].Value + ": " + match.Groups["message"].Value,
                code = match.Groups["code"].Value,
                message = match.Groups["message"].Value
            }), [])
            : new(JsonSerializer.Serialize(new { error = content }), []);
    }

    public async ValueTask DisposeAsync()
    {
        List<Exception> errors = [];
        try { await client.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { errors.Add(ex); }
        try { await server.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { errors.Add(ex); }
        try { await tools.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { errors.Add(ex); }
        try { await serverLoop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { errors.Add(ex); }
        if (errors.Count > 0) throw new AggregateException("MCP 会话清理失败。", errors);
    }
}
