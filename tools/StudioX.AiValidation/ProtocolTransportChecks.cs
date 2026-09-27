using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using StudioX.Application;
using StudioX.Foundation;

/// <summary>离线验证协议拆分后的事件边界、工具归并、用量解析和 HTTP 回退。</summary>
internal static class ProtocolTransportChecks
{
    private static readonly AiSettings Settings = new(ReasoningEffort: "max");
    private static readonly AiChatRequest Request = new([new AiChatMessage("user", "检查工程")]);

    internal static async Task RunAsync(Action<bool, string> check)
    {
        await CheckEventBoundariesAsync(check);
        await CheckToolOrderingAsync(check);
        await CheckUsageAsync(check);
        await CheckHttpFallbackAsync(check);
        await CheckCancellationAsync(check);
        await CheckMalformedStreamAsync(check);
        await CheckClientOwnershipAsync(check);
        await CheckToolCatalogAsync(check);
    }

    private static async Task CheckEventBoundariesAsync(Action<bool, string> check)
    {
        // 一字节读取会切开中文的 UTF-8 编码；SSE data 行还会跨网络块和多行 JSON。
        const string events = "\uFEFF: heartbeat\r\n" +
            "event: completion\r\n" +
            "data: {\"model\":\"deepseek-flash\",\r\n" +
            "data: \"choices\":[{\"delta\":{\"content\":\"中🙂文\",\"reasoning_content\":\"核对\"},\"finish_reason\":\"stop\"}]}\r\n\r\n" +
            "data: [DONE]";
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Sse(events))));
        using var chat = new AiChatClient(_ => "offline-test-key", http);
        var updates = new List<AiStreamUpdate>();
        var response = await chat.CompleteStreamingAsync(Settings, Request, updates.Add);
        check(response.Content == "中🙂文" && response.ReasoningContent == "核对" &&
            response.Model == "deepseek-flash" && response.FinishReason == "stop" &&
            updates.Select(update => update.Kind).SequenceEqual([
                AiStreamUpdateKind.ReasoningDelta, AiStreamUpdateKind.ContentDelta]),
            "SSE handles Unicode byte fragments, BOM, comments, multiline data, and final event without newline");
    }

    private static async Task CheckToolOrderingAsync(Action<bool, string> check)
    {
        const string events = """
            data: {"choices":[{"delta":{"tool_calls":[{"index":2,"id":"second","type":"function","function":{"name":"project_","arguments":"{\"value\":"}},{"index":0,"id":"first","type":"function","function":{"name":"project_info","arguments":"{}"}}]}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":2,"function":{"name":"search","arguments":"42}"}}]},"finish_reason":"tool_calls"}]}

            data: {"choices":[],"usage":{"prompt_cache_hit_tokens":0}}

            data: [DONE]

            """;
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Sse(events))));
        using var chat = new AiChatClient(_ => "offline-test-key", http);
        var updates = new List<AiStreamUpdate>();
        var response = await chat.CompleteStreamingAsync(Settings, Request, updates.Add);
        check(response.ToolCalls.SequenceEqual([
                new AiToolCall("first", "project_info", "{}"),
                new AiToolCall("second", "project_search", "{\"value\":42}")]) &&
            response.Content is null && response.FinishReason == "tool_calls" &&
            updates.Any(update => update.ToolCallIndex == 2 && update.ToolName == "project_search"),
            "tool fragments are grouped and returned by API index rather than arrival order");
        check(response.Usage == new AiTokenUsage(null, null, null, 0, null),
            "usage-only SSE preserves a reported zero cache hit and unknown remaining fields");
    }

    private static async Task CheckUsageAsync(Action<bool, string> check)
    {
        foreach (var usage in new[] { "null", "{}", "{\"prompt_tokens\":17}" })
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(
                "{\"choices\":[{\"message\":{\"content\":\"ok\"}}],\"usage\":" + usage + "}"))));
            using var chat = new AiChatClient(_ => "offline-test-key", http);
            var response = await chat.CompleteAsync(Settings, Request);
            check(usage.Contains("17", StringComparison.Ordinal)
                    ? response.Usage == new AiTokenUsage(17, null, null)
                    : response.Usage is null,
                "missing usage fields stay unknown: " + usage);
        }

        foreach (var invalidCount in new[] { "-1", "\"10\"", "2147483648" })
        {
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(
                "{\"choices\":[{\"message\":{\"content\":\"ok\"}}],\"usage\":{\"prompt_cache_hit_tokens\":" + invalidCount + "}}"))));
            using var chat = new AiChatClient(_ => "offline-test-key", http);
            await ExpectCodeAsync("AI_RESPONSE_FORMAT", () => chat.CompleteAsync(Settings, Request));
        }
        check(true, "negative, textual, and overflowing API token counts are rejected");
    }

    private static async Task CheckHttpFallbackAsync(Action<bool, string> check)
    {
        var requestModes = new List<bool>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            requestModes.Add(body.RootElement.GetProperty("stream").GetBoolean());
            return requestModes.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.NotImplemented)
                : Json("""
                    {"choices":[{"message":{"content":"兼容结果","reasoning_content":"核对完成"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"prompt_cache_hit_tokens":8,"prompt_cache_miss_tokens":2}}
                    """);
        }));
        using var chat = new AiChatClient(_ => "offline-test-key", http);
        var updates = new List<AiStreamUpdate>();
        var response = await chat.CompleteStreamingAsync(Settings with
        {
            BaseUrl = "https://example.test/v1"
        },
            Request, updates.Add);
        check(requestModes.SequenceEqual([true, false]) && response.Content == "兼容结果" &&
            updates.Select(update => update.Kind).SequenceEqual([
                AiStreamUpdateKind.ReasoningDelta, AiStreamUpdateKind.ContentDelta, AiStreamUpdateKind.Usage]),
            "unsupported compatible SSE falls back exactly once and still reports response and actual usage");

        var officialRequests = 0;
        using var officialHttp = new HttpClient(new Handler((_, _) =>
        {
            officialRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotImplemented));
        }));
        using var officialChat = new AiChatClient(_ => "offline-test-key", officialHttp);
        await ExpectCodeAsync("AI_HTTP", () => officialChat.CompleteStreamingAsync(Settings, Request, _ => { }));
        check(officialRequests == 1, "official endpoint HTTP errors retain diagnostics without an implicit retry");
    }

    private static async Task CheckCancellationAsync(Action<bool, string> check)
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var requests = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            return Task.FromResult(Json("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}"));
        }));
        using var chat = new AiChatClient(_ => "offline-test-key", http);
        var wasCancelled = false;
        try
        {
            await chat.CompleteStreamingAsync(Settings, Request, _ => { }, cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
        }
        check(wasCancelled && requests == 0, "caller cancellation remains cancellation and prevents an HTTP request");
    }

    private static async Task CheckMalformedStreamAsync(Action<bool, string> check)
    {
        var invalidUtf8 = Encoding.UTF8.GetBytes("data: ").Concat(new byte[] { 0xc3, 0x28, 10, 10 }).ToArray();
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Sse(invalidUtf8))));
        using var chat = new AiChatClient(_ => "offline-test-key", http);
        await ExpectCodeAsync("AI_RESPONSE_FORMAT", () => chat.CompleteStreamingAsync(Settings, Request, _ => { }));
        check(true, "invalid UTF-8 SSE is rejected before an assistant response can be persisted");
    }

    private static async Task CheckClientOwnershipAsync(Action<bool, string> check)
    {
        using var http = new HttpClient(new Handler((_, _) =>
            Task.FromResult(Json("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}"))));
        var chat = new AiChatClient(_ => "offline-test-key", http);
        chat.Dispose();
        using var probe = await http.GetAsync("https://example.test/probe");
        check(probe.IsSuccessStatusCode, "disposing the chat adapter does not dispose an injected HTTP client");
    }

    private static async Task CheckToolCatalogAsync(Action<bool, string> check)
    {
        var receivedToolCount = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            receivedToolCount = body.RootElement.GetProperty("tools").GetArrayLength();
            return Json("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
        }));
        using var chat = new AiChatClient(_ => "offline-test-key", http);
        var tools = Enumerable.Range(0, 100)
            .Select(index => new AiToolDefinition("tool_" + index, "offline test", "{\"type\":\"object\"}"))
            .ToArray();
        await chat.CompleteAsync(Settings, Request with
        {
            Tools = tools
        });
        check(receivedToolCount == tools.Length,
            "tool discovery above 64 definitions is transmitted without an artificial catalog count limit");
    }

    private static async Task ExpectCodeAsync(string code, Func<Task<AiChatResponse>> execute)
    {
        try
        {
            await execute();
        }
        catch (StudioXException error) when (error.Code == code)
        {
            return;
        }

        throw new InvalidOperationException("Expected StudioXException " + code);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Sse(string body) => Sse(Encoding.UTF8.GetBytes(body));

    private static HttpResponseMessage Sse(byte[] bytes)
    {
        var content = new StreamContent(new ByteFragmentStream(bytes));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class ByteFragmentStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
