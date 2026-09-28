namespace StudioX.EspressifRegistryValidation;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

/// <summary>模拟官方 MCP HTTP 契约，核对只发送公开工具参数，不需要本地文件或真实凭证。</summary>
internal sealed class RegistryHttpFixture : HttpMessageHandler
{
    public int Initializations
    {
        get; private set;
    }
    public int Calls
    {
        get; private set;
    }
    public string? ProtocolVersion
    {
        get; private set;
    }
    public string? LastTool
    {
        get; private set;
    }
    public JsonElement LastArguments
    {
        get; private set;
    }
    public bool AllRequestsAnonymous { get; private set; } = true;
    public bool AllRequestsOfficial { get; private set; } = true;
    public bool Disposed
    {
        get; private set;
    }
    public int? FailureStatus
    {
        get; set;
    }
    public bool LargeResponse
    {
        get; set;
    }
    public bool OversizedResponse
    {
        get; set;
    }
    public bool ToolError
    {
        get; set;
    }
    public bool WaitForCancellation
    {
        get; set;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        AllRequestsAnonymous &= request.Headers.Authorization is null;
        AllRequestsOfficial &= request.RequestUri?.AbsoluteUri == "https://components.espressif.com/mcp/";
        if (request.Method != HttpMethod.Post)
        {
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        var message = document.RootElement;
        if (!message.TryGetProperty("id", out var id))
        {
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
        var method = message.GetProperty("method").GetString();
        if (method == "initialize")
        {
            Initializations++;
            ProtocolVersion = message.GetProperty("params").GetProperty("protocolVersion").GetString();
            return Response(id, new
            {
                protocolVersion = "2025-06-18",
                capabilities = new
                {
                    tools = new
                    {
                    }
                },
                serverInfo = new
                {
                    name = "registry-contract-fixture",
                    version = "1"
                }
            });
        }
        if (method == "tools/call")
        {
            Calls++;
            var parameters = message.GetProperty("params");
            LastTool = parameters.GetProperty("name").GetString();
            LastArguments = parameters.GetProperty("arguments").Clone();
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            if (FailureStatus is { } failure)
            {
                var failed = new HttpResponseMessage((HttpStatusCode)failure);
                failed.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
                return failed;
            }
            if (ToolError)
            {
                return Response(id, new
                {
                    isError = true,
                    content = new[] { new { type = "text", text = "official component query error" } }
                });
            }
            object payload = LastTool == "search_components"
                ? Enumerable.Range(0, LargeResponse ? 30 : 1).Select(index => new
                {
                    namespace_name = "espressif",
                    component_name = index == 0 ? "button" : "button" + index,
                    description = "官方按键组件"
                }).ToArray()
                : "# button\nOfficial component documentation. [Source](https://components.espressif.com/components/espressif/button)\n" +
                    (OversizedResponse ? new string('x', 1_048_576) :
                        LargeResponse ? new string('x', 20_000) : "Requires ESP-IDF >= 5.0.");
            return Response(id, new
            {
                content = new[] { new { type = "text", text = JsonSerializer.Serialize(new { result = payload }) } },
                structuredContent = new
                {
                    result = payload
                },
                isError = false
            });
        }
        throw new InvalidOperationException("Unexpected MCP fixture method: " + method);
    }

    private static HttpResponseMessage Response(JsonElement id, object result) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            result
        }), Encoding.UTF8, "application/json")
    };

    protected override void Dispose(bool disposing)
    {
        Disposed = disposing;
        base.Dispose(disposing);
    }
}
