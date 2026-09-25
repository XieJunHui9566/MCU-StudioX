using System.Net;
using System.Text;
using System.Text.Json;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine("PASS " + description);
    checks++;
}

var root = Path.Combine(Path.GetTempPath(), "studiox-ai-validation-" + Guid.NewGuid().ToString("N"));
var captured = new List<JsonDocument>();
Directory.CreateDirectory(root);
try
{
    var deepSeek = new AiSettings(ReasoningEffort: "max");
    using var http = new HttpClient(new StubHandler(async request =>
    {
        var body = await request.Content!.ReadAsStringAsync();
        captured.Add(JsonDocument.Parse(body));
        return Json("""
            {"model":"deepseek-flash","choices":[{"message":{"role":"assistant","content":"完成","reasoning_content":"检查后回答"},"finish_reason":"stop"}],"usage":{"prompt_tokens":123,"completion_tokens":45,"total_tokens":168,"prompt_cache_hit_tokens":90,"prompt_cache_miss_tokens":33}}
            """);
    }));
    using var chat = new AiChatClient(_ => "offline-test-key", http);
    var response = await chat.CompleteAsync(deepSeek, new AiChatRequest([new AiChatMessage("user", "你好")]));
    var firstRequest = captured[0].RootElement;
    Check(firstRequest.GetProperty("reasoning_effort").GetString() == "max" &&
        !firstRequest.TryGetProperty("thinking", out _),
        "official DeepSeek sends max reasoning effort without disabled thinking");
    Check(response.Usage == new AiTokenUsage(123, 45, 168, 90, 33) &&
        response.ReasoningContent == "检查后回答", "Chat Completions usage and reasoning content are parsed");

    var streamUpdates = new List<AiStreamUpdate>();
    using var streamingHttp = new HttpClient(new StubHandler(async request =>
    {
        using var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Check(sent.RootElement.GetProperty("stream").GetBoolean() &&
            sent.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean(),
            "streaming request asks for incremental output and final usage");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Sse(string.Join("\r\n\r\n", new[]
            {
                "data: {\"model\":\"deepseek-flash\",\"choices\":[{\"delta\":{\"reasoning_content\":\"先检查\"},\"finish_reason\":null}]}",
                "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"工程\",\"content\":\"已检查\"}}]}",
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call-\",\"type\":\"function\",\"function\":{\"name\":\"project_\",\"arguments\":\"{\\\"a\\\":\"}}]}}]}",
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"1\",\"function\":{\"name\":\"info\",\"arguments\":\"1}\"}}]},\"finish_reason\":\"tool_calls\"}]}",
                "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":130,\"completion_tokens\":21,\"total_tokens\":151,\"prompt_cache_hit_tokens\":100,\"prompt_cache_miss_tokens\":30}}",
                "data: [DONE]"
            }) + "\r\n\r\n")
        };
    }));
    using var streamingChat = new AiChatClient(_ => "offline-test-key", streamingHttp);
    var streamed = await streamingChat.CompleteStreamingAsync(deepSeek,
        new AiChatRequest([new AiChatMessage("user", "检查工程")]), streamUpdates.Add);
    Check(streamed.ReasoningContent == "先检查工程" && streamed.Content == "已检查" &&
        streamed.ToolCalls.Single() == new AiToolCall("call-1", "project_info", "{\"a\":1}") &&
        streamed.Usage == new AiTokenUsage(130, 21, 151, 100, 30) && streamed.FinishReason == "tool_calls",
        "streaming response assembles reasoning, answer, tool-call fragments, and usage");
    Check(streamUpdates.Where(update => update.Kind == AiStreamUpdateKind.ReasoningDelta)
            .Select(update => update.Text).SequenceEqual(["先检查", "工程"]) &&
        streamUpdates.Any(update => update.Kind == AiStreamUpdateKind.ContentDelta && update.Text == "已检查") &&
        streamUpdates.Any(update => update.Kind == AiStreamUpdateKind.ToolCall && update.ToolName == "project_info") &&
        streamUpdates.Any(update => update.Kind == AiStreamUpdateKind.Usage && update.Usage?.PromptTokens == 130),
        "streaming updates expose live reasoning, answer, tool activity, and token usage");

    using var jsonStreamHttp = new HttpClient(new StubHandler(_ => Task.FromResult(Json("""
        {"choices":[{"message":{"role":"assistant","content":"兼容响应","reasoning_content":"完整思考"},"finish_reason":"stop"}]}
        """))));
    using var jsonStreamChat = new AiChatClient(_ => "offline-test-key", jsonStreamHttp);
    var jsonFallbackUpdates = new List<AiStreamUpdate>();
    var jsonFallback = await jsonStreamChat.CompleteStreamingAsync(deepSeek,
        new AiChatRequest([new AiChatMessage("user", "检查兼容响应")]), jsonFallbackUpdates.Add);
    Check(jsonFallback.Content == "兼容响应" && jsonFallback.ReasoningContent == "完整思考" &&
        jsonFallbackUpdates.Select(update => update.Kind).SequenceEqual([
            AiStreamUpdateKind.ReasoningDelta, AiStreamUpdateKind.ContentDelta]),
        "streaming transport accepts a compatible endpoint's full JSON response");

    using var truncatedHttp = new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = Sse("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n")
    })));
    using var truncatedChat = new AiChatClient(_ => "offline-test-key", truncatedHttp);
    await ExpectCodeAsync("AI_RESPONSE_FORMAT", () => truncatedChat.CompleteStreamingAsync(deepSeek,
        new AiChatRequest([new AiChatMessage("user", "检查截断")]), _ => { }));
    Check(true, "truncated SSE is rejected instead of saving a partial assistant answer");

    var toolReplay = new AiChatRequest([
        new AiChatMessage("user", "列出工程"),
        new AiChatMessage("assistant", null, ToolCalls: [new AiToolCall("call-1", "project_info", "{}")],
            ReasoningContent: "先检查工程"),
        new AiChatMessage("tool", "{\"name\":\"demo\"}", ToolCallId: "call-1")
    ]);
    _ = await chat.CompleteAsync(deepSeek, toolReplay);
    var serializedMessages = captured[1].RootElement.GetProperty("messages");
    Check(serializedMessages[1].GetProperty("reasoning_content").GetString() == "先检查工程" &&
        serializedMessages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString() == "call-1" &&
        serializedMessages[2].GetProperty("tool_call_id").GetString() == "call-1",
        "reasoning content accompanies assistant tool calls in the next request");

    var pageBytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3 };
    var imageReplay = toolReplay with
    {
        Images = [new AiRequestImage("image/png", pageBytes, "原理图第 2 页")]
    };
    _ = await chat.CompleteAsync(deepSeek, imageReplay);
    var imageMessages = captured[2].RootElement.GetProperty("messages");
    var imageContent = imageMessages[3].GetProperty("content");
    Check(imageReplay.Messages.Count == 3 && imageMessages.GetArrayLength() == 4 &&
        imageMessages[3].GetProperty("role").GetString() == "user" &&
        imageContent[1].GetProperty("text").GetString() == "原理图第 2 页" &&
        imageContent[2].GetProperty("type").GetString() == "image_url" &&
        imageContent[2].GetProperty("image_url").GetProperty("url").GetString() ==
            "data:image/png;base64," + Convert.ToBase64String(pageBytes),
        "page image is an ephemeral vision content part after the MCP tool message");
    await ExpectCodeAsync("AI_IMAGE_SIZE", () => chat.CompleteAsync(deepSeek,
        toolReplay with { Images = [new AiRequestImage("image/png", new byte[900 * 1024], "过大页面")] }));
    Check(true, "oversized inline page images are rejected before an API call");
    await ExpectCodeAsync("AI_REQUEST_SIZE", () => chat.CompleteAsync(deepSeek,
        new AiChatRequest(Enumerable.Range(0, 128)
            .Select(_ => new AiChatMessage("user", "x")).ToArray(),
            Images: [new AiRequestImage("image/png", pageBytes, "额外页面")])));
    Check(true, "ephemeral image message counts against the API message limit");

    using var genericHttp = new HttpClient(new StubHandler(async request =>
    {
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Check(!document.RootElement.TryGetProperty("reasoning_effort", out _),
            "compatible non DeepSeek endpoints omit proprietary reasoning effort");
        return Json("""
            {"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}
            """);
    }));
    using var genericChat = new AiChatClient(_ => "offline-test-key", genericHttp);
    _ = await genericChat.CompleteAsync(deepSeek with { BaseUrl = "https://example.test/v1" },
        new AiChatRequest([new AiChatMessage("user", "hello")]));
    await ExpectCodeAsync("AI_VISION_UNAVAILABLE", () => genericChat.CompleteAsync(
        deepSeek with { BaseUrl = "https://example.test/v1" }, imageReplay));
    Check(true, "unknown model endpoints never receive page images without verified vision support");
    Check(AiSettingsService.EffectiveContextWindowTokens(deepSeek) == 1_000_000 &&
        AiSettingsService.EffectiveContextWindowTokens(deepSeek with { BaseUrl = "https://example.test/v1" }) is null,
        "context window is known only for documented DeepSeek models by default");

    var legacySettingsDirectory = Path.Combine(root, "legacy-settings");
    Directory.CreateDirectory(legacySettingsDirectory);
    await File.WriteAllTextAsync(Path.Combine(legacySettingsDirectory, "ai.json"),
        """{"formatVersion":1,"baseUrl":"https://api.deepseek.com","model":"deepseek-flash","timeoutSeconds":120}""");
    var migratedSettings = await new AiSettingsService(legacySettingsDirectory).LoadAsync();
    Check(migratedSettings.ReasoningEffort == "high" && migratedSettings.ContextWindowTokens is null &&
        migratedSettings.Model == "deepseek-flash",
        "legacy four-field ai.json loads with default reasoning and unknown explicit context window");

    var projectA = await CreateProjectAsync(Path.Combine(root, "project-a"));
    var projectB = await CreateProjectAsync(Path.Combine(root, "project-b"));
    var turns = new[]
    {
        new AiAgentTurn("检查工程信息", "工程已检查", "已经收到工具结果", [
            new AiChatMessage("user", "检查工程信息"),
            new AiChatMessage("assistant", null,
                ToolCalls: [new AiToolCall("call-project", "project_info", "{}")],
                ReasoningContent: "先调用工具"),
            new AiChatMessage("tool", "{\"name\":\"demo\"}", ToolCallId: "call-project"),
            new AiChatMessage("assistant", "工程已检查", ReasoningContent: "已经收到工具结果")
        ]),
        new AiAgentTurn("继续", "继续完成", "接着回答", [
            new AiChatMessage("user", "继续"),
            new AiChatMessage("assistant", "继续完成", ReasoningContent: "接着回答")
        ])
    };

    var store = new AiConversationStore(Path.Combine(root, "data"));
    var inA = await store.CreateAsync(projectA);
    var inB = await store.CreateAsync(projectB);
    var savedA = await store.SaveAsync(projectA, inA with
    {
        Title = "检查工程信息", Turns = turns,
        LastPromptTokens = 180
    });
    Check((await store.ListAsync(projectA)).Single().Id == inA.Id &&
        (await store.ListAsync(projectB)).Single().Id == inB.Id &&
        (await store.LoadAsync(projectA, inA.Id)).Turns.Count == 2,
        "conversation records remain isolated per project and survive reload");
    await ExpectCodeAsync("AI_HISTORY_MISSING", () => store.LoadAsync(projectB, inA.Id));
    Check(true, "project B cannot load project A conversation");

    var oversizedTurns = Enumerable.Range(0, 34)
        .Select(_ => new AiAgentTurn("question", new string('a', 16_000))).ToArray();
    await ExpectCodeAsync("AI_HISTORY_SIZE", () => store.SaveAsync(projectA,
        savedA with { Turns = oversizedTurns }));
    Check((await store.LoadAsync(projectA, inA.Id)).Turns.Count == 2,
        "512 KiB limit rejects an oversized save without replacing prior history");

    var crowdedTurns = Enumerable.Range(0, 3).Select(index =>
    {
        var question = "问题 " + index;
        var answer = "回答 " + index;
        var callId = "call-" + index;
        return new AiAgentTurn(question, answer, new string('r', 40_000), [
            new AiChatMessage("user", question),
            new AiChatMessage("assistant", null,
                ToolCalls: [new AiToolCall(callId, "project_read_file", "{}")]),
            new AiChatMessage("tool", new string('p', 190_000), ToolCallId: callId),
            new AiChatMessage("assistant", answer)
        ]);
    }).ToArray();
    var compacted = await store.SaveAsync(projectA, savedA with { Turns = crowdedTurns });
    var reloadedCompacted = await store.LoadAsync(projectA, inA.Id);
    Check(compacted.Turns.Count == 3 && reloadedCompacted.Turns.Count == 3 &&
        compacted.Turns[0].ProtocolMessages is null && compacted.Turns[0].ReasoningContent is null &&
        reloadedCompacted.Turns[0].ProtocolMessages is null &&
        reloadedCompacted.Turns[1].ProtocolMessages is not null &&
        reloadedCompacted.Turns[2].ProtocolMessages is not null &&
        reloadedCompacted.Turns.Select(turn => (turn.User, turn.Assistant))
            .SequenceEqual(crowdedTurns.Select(turn => (turn.User, turn.Assistant))),
        "oversized history discards the oldest replay data while preserving every visible turn");

    var largeSingleTurn = new AiAgentTurn("单轮问题", "单轮可见回答", new string('r', 40_000), [
        new AiChatMessage("user", "单轮问题"),
        new AiChatMessage("assistant", null,
            ToolCalls: [new AiToolCall("large-call", "project_read_file", "{}")]),
        new AiChatMessage("tool", new string('p', 600_000), ToolCallId: "large-call"),
        new AiChatMessage("assistant", "单轮可见回答")
    ]);
    var compactedSingle = await store.SaveAsync(projectA, compacted with { Turns = [largeSingleTurn] });
    var reloadedSingle = await store.LoadAsync(projectA, inA.Id);
    Check(compactedSingle.Turns.Single().ProtocolMessages is null &&
        compactedSingle.Turns.Single().ReasoningContent is null &&
        reloadedSingle.Turns.Single().User == largeSingleTurn.User &&
        reloadedSingle.Turns.Single().Assistant == largeSingleTurn.Assistant,
        "a single oversized tool round remains readable after its replay data is compacted");

    for (var index = 1; index < AiConversationStore.MaxConversationsPerProject; index++)
        _ = await store.CreateAsync(projectB);
    await ExpectCodeAsync("AI_HISTORY_COUNT", () => store.CreateAsync(projectB));
    Check((await store.ListAsync(projectB)).Count == AiConversationStore.MaxConversationsPerProject,
        "64 conversation cap rejects new history without pruning existing records");

    await CacheOptimizationChecks.RunAsync(projectA, root, Check);

    Console.WriteLine($"PASS {checks} offline AI checks; no live API or toolchain access.");
}
finally
{
    foreach (var document in captured) document.Dispose();
    // 清理本次唯一临时目录中创建的少量 JSON 文件，不触及已有用户数据。
    var safeRoot = Path.GetFullPath(root);
    var temp = Path.GetFullPath(Path.GetTempPath());
    if (safeRoot.StartsWith(temp, StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(safeRoot).StartsWith("studiox-ai-validation-", StringComparison.Ordinal) &&
        Directory.Exists(safeRoot))
    {
        foreach (var file in Directory.EnumerateFiles(safeRoot, "*", SearchOption.AllDirectories)) File.Delete(file);
        foreach (var directory in Directory.EnumerateDirectories(safeRoot, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length)) Directory.Delete(directory);
        Directory.Delete(safeRoot);
    }
}

static async Task<string> CreateProjectAsync(string path)
{
    Directory.CreateDirectory(Path.Combine(path, ".studiox"));
    await JsonStore.WriteAsync(Path.Combine(path, ".studiox", "project.json"),
        new ProjectManifest(1, "demo", "demo.pack", "1.0.0", "offline", "demo", "blank", "gcc", "1.0.0", "gcc"));
    return path;
}

static async Task ExpectCodeAsync<T>(string code, Func<Task<T>> run)
{
    try { _ = await run(); }
    catch (StudioXException error) when (error.Code == code) { return; }
    throw new Exception($"Expected StudioXException {code}");
}

static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
{
    Content = new StringContent(body, Encoding.UTF8, "application/json")
};

static HttpContent Sse(string body)
{
    var content = new StreamContent(new ChunkedReadStream(Encoding.UTF8.GetBytes(body)));
    content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("text/event-stream");
    return content;
}

sealed class ChunkedReadStream(byte[] bytes) : MemoryStream(bytes)
{
    public override int Read(byte[] buffer, int offset, int count) =>
        base.Read(buffer, offset, Math.Min(count, 7));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(buffer.Length, 7)], cancellationToken);
}

sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        respond(request);
}
