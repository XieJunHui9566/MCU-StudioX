using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Foundation;

internal static class CustomMcpChecks
{
    public static async Task RunAsync(WorkbenchService services, StudioXMcpSession denied, Action<bool, string> check)
    {
        await using var fixture = new ServerFixture();
        var server = new AiMcpServer("offline-fixture", "离线 MCP", Endpoint: fixture.Endpoint);
        await services.AiMcp.SaveAsync([server]);
        var listing = await denied.CallToolAsync("mcp_server_list", "{}");
        var refused = await denied.CallToolAsync("mcp_server_tools", """{"serverId":"offline-fixture"}""");
        check(listing.Contains("offline-fixture") && refused.Contains("MCP_APPROVAL_DENIED") && fixture.Requests == 0,
            "custom MCP discovery lists configured services but denied connection never contacts server");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using (var client = await services.AiMcp.ConnectAsync(server, timeout.Token, "fixture-bearer"))
        {
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            var result = await client.CallToolAsync("echo", new Dictionary<string, object?> { ["message"] = "你好 MCP" }, cancellationToken: timeout.Token);
            check(tools.Single().Name == "echo" && result.IsError != true && fixture.SawBearer,
                "real Streamable HTTP client initializes, lists and invokes a remote tool with an isolated Bearer credential");
        }

        var approvals = new AllowRecorder();
        await using var allowed = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services, denied.Project, approvals));
        var schema = await allowed.CallToolAsync("mcp_server_tools", """{"serverId":"offline-fixture","query":"echo"}""");
        var output = await allowed.CallToolAsync("mcp_server_call", """{"serverId":"offline-fixture","name":"echo","arguments":{"message":"用户输入"}}""");
        check(schema.Contains("echo") && output.Contains("isError\":false") &&
            approvals.Requests.Count == 2 && approvals.Requests.All(request => request.Permission == StudioXMcpPermission.CustomMcp),
            "Agent custom MCP connection and invocation both use host approval and return server output");
        await using (var replacement = new ServerFixture())
        {
            var oldCalls = fixture.ToolCalls;
            await services.AiMcp.SaveAsync([server with { Endpoint = replacement.Endpoint }]);
            var changed = await allowed.CallToolAsync("mcp_server_call", """{"serverId":"offline-fixture","name":"echo","arguments":{}}""");
            check(changed.Contains("isError\":false") && replacement.ToolCalls == 1 && fixture.ToolCalls == oldCalls,
                "changing a configured endpoint reconnects and never invokes the cached old endpoint");
        }
        await services.AiMcp.SaveAsync([server with { Enabled = false }]);
        var previous = fixture.ToolCalls;
        var disabled = await allowed.CallToolAsync("mcp_server_call", """{"serverId":"offline-fixture","name":"echo","arguments":{}}""");
        check(disabled.Contains("MCP_SERVER") && fixture.ToolCalls == previous,
            "disabling a configured MCP prevents use of an existing client");
        var rejected = false;
        try
        {
            AiMcpSettingsService.Validate([server with { Endpoint = "http://remote.example/mcp" }]);
        }
        catch (StudioXException) { rejected = true; }
        check(rejected, "custom MCP rejects plaintext remote HTTP while allowing explicit local HTTP");
        await using (var redirectTarget = new ServerFixture())
        {
            await using (var redirect = new ServerFixture(redirectTarget.Endpoint))
            {
                var redirectRejected = false;
                using var redirectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await using var redirected = await services.AiMcp.ConnectAsync(server with
                    {
                        Endpoint = redirect.Endpoint
                    }, redirectTimeout.Token, "fixture-bearer");
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    redirectRejected = true;
                }
                check(redirectRejected && redirect.Requests > 0 && redirectTarget.Requests == 0,
                    "custom MCP rejects HTTP redirects without contacting another endpoint or forwarding credentials");
            }
        }
        await services.AiMcp.SaveAsync([]);
        var program = Environment.ProcessPath!;
        var arguments = Path.GetFileNameWithoutExtension(program).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? new[] { typeof(CustomMcpChecks).Assembly.Location, "--mcp-echo-server" } : new[] { "--mcp-echo-server" };
        var stdio = new AiMcpServer("stdio-fixture", "本机进程", "stdio", Command: program, Arguments: arguments);
        await using (var client = await services.AiMcp.ConnectAsync(stdio, timeout.Token))
        {
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            var result = await client.CallToolAsync("echo", new Dictionary<string, object?> { ["text"] = "stdio OK" }, cancellationToken: timeout.Token);
            check(tools.Single().Name == "echo" && result.IsError != true,
                "real stdio client launches a native fixture without shell or inherited secret environment and disposes it");
        }
    }

    private sealed class AllowRecorder : IStudioXMcpAuthorizer
    {
        public List<StudioXMcpApprovalRequest> Requests { get; } = [];
        public Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token)
        {
            Requests.Add(request);
            return Task.FromResult(true);
        }
    }

    private sealed class ServerFixture : IAsyncDisposable
    {
        private readonly HttpListener listener = new();
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;
        public string Endpoint
        {
            get;
        }
        public int Requests
        {
            get; private set;
        }
        public int ToolCalls
        {
            get; private set;
        }
        public bool SawBearer
        {
            get; private set;
        }
        private readonly string? redirectTo;
        public ServerFixture(string? redirectTo = null)
        {
            this.redirectTo = redirectTo;
            using var port = new TcpListener(IPAddress.Loopback, 0);
            port.Start();
            var number = ((IPEndPoint)port.LocalEndpoint).Port;
            port.Stop();
            Endpoint = $"http://127.0.0.1:{number}/mcp/";
            listener.Prefixes.Add(Endpoint);
            listener.Start();
            loop = RunAsync();
        }
        private async Task RunAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var context = await listener.GetContextAsync().WaitAsync(stop.Token);
                    try
                    {
                        Requests++;
                        SawBearer |= context.Request.Headers["Authorization"] == "Bearer fixture-bearer";
                        if (redirectTo is not null)
                        {
                            context.Response.StatusCode = 307;
                            context.Response.RedirectLocation = redirectTo;
                            continue;
                        }
                        using var message = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: stop.Token);
                        var root = message.RootElement;
                        if (!root.TryGetProperty("id", out var id))
                        {
                            context.Response.StatusCode = 202;
                            continue;
                        }
                        var method = root.GetProperty("method").GetString();
                        object? result = method switch
                        {
                            "initialize" => new
                            {
                                protocolVersion = root.GetProperty("params").GetProperty("protocolVersion").GetString(),
                                capabilities = new
                                {
                                    tools = new
                                    {
                                    }
                                },
                                serverInfo = new
                                {
                                    name = "offline-fixture",
                                    version = "1.0"
                                }
                            },
                            "tools/list" => new
                            {
                                tools = new[] { new { name = "echo", description = "Fixture echo", inputSchema = new
                                { type = "object", properties = new { message = new { type = "string" } } } } }
                            },
                            "tools/call" => new { content = new[] { new { type = "text", text = root.GetProperty("params").GetProperty("arguments").GetRawText() } }, isError = false },
                            _ => null
                        };
                        if (method == "tools/call")
                        {
                            ToolCalls++;
                        }
                        var payload = result is null
                            ? JsonSerializer.SerializeToUtf8Bytes(new
                            {
                                jsonrpc = "2.0",
                                id = id.Clone(),
                                error = new
                                {
                                    code = -32601,
                                    message = "Method not found"
                                }
                            })
                            : JsonSerializer.SerializeToUtf8Bytes(new
                            {
                                jsonrpc = "2.0",
                                id = id.Clone(),
                                result
                            });
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = payload.Length;
                        await context.Response.OutputStream.WriteAsync(payload, stop.Token);
                    }
                    finally { context.Response.Close(); }
                }
            }
            catch (Exception ex) when (stop.IsCancellationRequested && ex is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Close();
            await loop;
            stop.Dispose();
        }
    }
}
