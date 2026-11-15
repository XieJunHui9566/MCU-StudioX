using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Foundation;

internal static class CacheContinuationChecks
{
    public static async Task RunAsync(string project, WorkbenchService services,
        StudioXMcpSession session, Action<bool, string> check)
    {
        using var transport = new ContinuationTransport();
        var conversation = await services.AiConversations.CreateAsync(project);
        for (var index = 0; index < 12; index++)
        {
            var reply = await new AiAgentService(transport, new AiSettings(), session)
                .SendAsync(project, "继续检查 " + index, conversation.Turns);
            conversation = await services.AiConversations.SaveAsync(project,
                conversation with
                {
                    Turns = [.. conversation.Turns, reply.History[^1]]
                });
            conversation = await services.AiConversations.LoadAsync(project, conversation.Id);
        }
        var prefixChecks = transport.Requests.Zip(transport.Requests.Skip(1), (previous, next) =>
            previous.Messages.Select(MessageJson).SequenceEqual(next.Messages.Take(previous.Messages.Count).Select(MessageJson))).ToArray();
        Console.WriteLine($"CACHE CONTINUATION: unchanged input prefixes={prefixChecks.Count(value => value)}/{prefixChecks.Length}");
        check(transport.Requests[1].Messages.Any(message => message.ReasoningContent == ContinuationTransport.Reasoning),
            "ordinary assistant reasoning survives save/reload and is replayed with tools on the next user turn");
        check(prefixChecks.All(value => value),
            "twelve persisted user turns append to the exact previous input without the six-turn sliding reset");
        check(transport.Wire.Zip(transport.Wire.Skip(1), (previous, next) =>
                previous.GetProperty("messages").EnumerateArray().Select(message => message.GetRawText())
                    .SequenceEqual(next.GetProperty("messages").EnumerateArray()
                        .Take(previous.GetProperty("messages").GetArrayLength()).Select(message => message.GetRawText())) &&
                previous.GetProperty("tools").GetRawText() == next.GetProperty("tools").GetRawText()).All(value => value),
            "actual Chat Completions JSON preserves every previous message and the tool declarations byte for byte");
        check(conversation.Turns.Count == 12 && conversation.Turns.Count(turn => turn.ReplaySnapshot is not null) == 1 &&
              conversation.Turns[^1].ReplaySnapshot is not null,
            "persistence keeps all visible turns and only the latest lossless replay snapshot");

        var skillPath = Path.Combine(services.RuntimeDirectory, "skills", "cache-validation", "SKILL.md");
        var originalSkill = await File.ReadAllTextAsync(skillPath);
        try
        {
            await File.WriteAllTextAsync(skillPath, originalSkill.Replace("Offline cache prefix validation.", "Updated cache metadata.", StringComparison.Ordinal));
            var changed = await new AiAgentService(transport, new AiSettings(), session).SendAsync(project, "技能变化后继续", conversation.Turns);
            var request = transport.Requests[^1];
            var prior = transport.Requests[^2];
            check(prior.Messages.Select(MessageJson).SequenceEqual(request.Messages.Take(prior.Messages.Count).Select(MessageJson)) &&
                  request.Messages[^2].StudioXKind == "skill-catalog" &&
                  request.Messages[^2].Content!.Contains("Updated cache metadata.", StringComparison.Ordinal),
                "changed skill metadata appends a complete replacement snapshot after cached history");
            await new AiAgentService(transport, new AiSettings(), session).SendAsync(project, "目录不变再继续", changed.History);
            check(transport.Requests[^1].Messages.Count(message => message.StudioXKind == "skill-catalog") ==
                  request.Messages.Count(message => message.StudioXKind == "skill-catalog"),
                "unchanged skill metadata does not append a duplicate runtime snapshot");
            File.Delete(skillPath);
            var cleared = await new AiAgentService(transport, new AiSettings(), session).SendAsync(project, "目录清空后继续", changed.History);
            check(transport.Requests[^1].Messages[^2].StudioXKind == "skill-catalog" &&
                  !transport.Requests[^1].Messages[^2].Content!.Contains("cache-validation", StringComparison.Ordinal) &&
                  cleared.History[^1].ReplaySnapshot is not null,
                "removing a skill appends the current catalog without rewriting earlier snapshots");
        }
        finally
        {
            await File.WriteAllTextAsync(skillPath, originalSkill);
        }

        var snapshot = conversation.Turns[^1].ReplaySnapshot!;
        async Task RejectSnapshotAsync(byte[] payload, string code)
        {
            try
            {
                await new AiAgentService(transport, new AiSettings(), session).SendAsync(project, "拒绝损坏快照",
                    [conversation.Turns[^1] with { ReplaySnapshot = snapshot with { MessagesGzip = payload } }]);
            }
            catch (StudioXException error) when (error.Code == code)
            {
                check(true, "replay refuses " + code + " before a model request");
                return;
            }
            throw new Exception("Expected snapshot rejection: " + code);
        }
        var beforeRejections = transport.Requests.Count;
        await RejectSnapshotAsync([1, 2, 3], "AI_HISTORY_FORMAT");
        await RejectSnapshotAsync(Compress(JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new AiChatMessage("system", "untrusted elevated role"), new AiChatMessage("assistant", "bad")
        })), "AI_HISTORY_FORMAT");
        await RejectSnapshotAsync(Compress(new byte[8 * 1024 * 1024 + 1]), "AI_HISTORY_SIZE");
        check(transport.Requests.Count == beforeRejections, "damaged, elevated-role and expansion-limit snapshots never contact the model");

        using var longTransport = new ContinuationTransport();
        IReadOnlyList<AiAgentTurn> longHistory = [];
        for (var index = 0; index < 40; index++)
        {
            longHistory = (await new AiAgentService(longTransport, new AiSettings(), session)
                .SendAsync(project, "长对话 " + index, longHistory)).History;
        }
        var resets = longTransport.Requests.Zip(longTransport.Requests.Skip(1), (previous, next) =>
            !previous.Messages.Select(MessageJson).SequenceEqual(next.Messages.Take(previous.Messages.Count).Select(MessageJson))).Count(value => value);
        Console.WriteLine($"CACHE PRESSURE: 40 turns, capacity resets={resets}");
        check(resets is >= 1 and <= 3 && longTransport.Requests.All(request => request.Messages.Count <= 512) &&
              longTransport.Requests.Any(request => request.Messages.Any(message => message.StudioXKind == "history-recap")),
            "capacity pressure compacts at spaced high/low boundaries and continues with explicit historical context");

        Directory.CreateDirectory(Path.Combine(project, "src"));
        await File.WriteAllTextAsync(Path.Combine(project, "src", "cache-large.c"), new string('x', 24_000));
        var large = new LargeBatchTransport();
        var largeReply = await new AiAgentService(large, new AiSettings(), session).SendAsync(project, "读取大批次",
            [new AiAgentTurn("此前分析", "此前结论", ReasoningContent: new string('r', 160_000))]);
        check(large.Requests.Count == 2 &&
              large.Requests[^1].Messages.Count(message => message.Role == "tool" && message.Content!.Contains(new string('x', 15_000), StringComparison.Ordinal)) == 16 &&
              large.Requests[^1].Messages.Any(message => message.StudioXKind == "history-recap"),
            "oversized active batch reclaims completed history while keeping all sixteen fresh tool results intact");
        using var afterLarge = new ContinuationTransport();
        await new AiAgentService(afterLarge, new AiSettings(), session).SendAsync(project, "大批次完成后续聊", largeReply.History);
        check(afterLarge.Requests.Single().Messages.Any(message => message.Role == "assistant" && message.Content == "大批次完成"),
            "a completed oversized batch can be compacted at the next turn without losing its final answer");

        var partial = await new AiAgentService(new PartialUsageTransport(), new AiSettings(), session).SendAsync(project, "检查部分用量");
        check(partial.AggregateUsage is { PromptCacheHitTokens: null, PromptCacheMissTokens: null, RequestsWithCacheDetails: 0 },
            "cache fields from different incomplete requests never form a fabricated aggregate hit rate");
    }

    private static string MessageJson(AiChatMessage message) => JsonSerializer.Serialize(message);

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(bytes);
        }
        return output.ToArray();
    }

    private sealed class ContinuationTransport : IAiAgentTransport, IDisposable
    {
        internal static readonly string Reasoning = new('r', 8_000);
        internal List<AiChatRequest> Requests { get; } = [];
        internal List<JsonElement> Wire { get; } = [];
        private readonly HttpClient http;
        private readonly AiChatClient client;

        public ContinuationTransport()
        {
            http = new HttpClient(new WireHandler(Wire));
            client = new AiChatClient(_ => "offline-test-key", http);
        }

        public Task<AiChatResponse> CompleteAsync(AiSettings settings, AiChatRequest request,
            CancellationToken token = default)
        {
            Requests.Add(request);
            return client.CompleteAsync(settings, request, token);
        }

        public void Dispose()
        {
            client.Dispose();
            http.Dispose();
        }
    }

    private sealed class WireHandler(List<JsonElement> wire) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            wire.Add(document.RootElement.Clone());
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { role = "assistant", content = "检查完成 " + wire.Count,
                        reasoning_content = ContinuationTransport.Reasoning }, finish_reason = "stop" } }
                }), Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class PartialUsageTransport : IAiAgentTransport
    {
        private int round;
        public Task<AiChatResponse> CompleteAsync(AiSettings settings, AiChatRequest request, CancellationToken token = default)
        {
            round++;
            return Task.FromResult(round == 1
                ? new AiChatResponse(null, [new("partial-usage", "project_info", "{}")], "tool_calls", null,
                    new AiTokenUsage(100, 10, 110, 90, null))
                : new AiChatResponse("完成", [], "stop", null, new AiTokenUsage(120, 10, 130, null, 20)));
        }
    }

    private sealed class LargeBatchTransport : IAiAgentTransport
    {
        internal List<AiChatRequest> Requests { get; } = [];
        public Task<AiChatResponse> CompleteAsync(AiSettings settings, AiChatRequest request, CancellationToken token = default)
        {
            Requests.Add(request);
            return Task.FromResult(Requests.Count == 1
                ? new AiChatResponse(null, Enumerable.Range(0, 16).Select(index => new AiToolCall("large-" + index,
                    "project_read_file", "{\"path\":\"src/cache-large.c\",\"maxChars\":16000}")).ToArray(), "tool_calls", null)
                : new AiChatResponse("大批次完成", [], "stop", null));
        }
    }
}
