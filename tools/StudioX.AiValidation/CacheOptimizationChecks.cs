using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;

internal static class CacheOptimizationChecks
{
    public static async Task RunAsync(string project, string temporaryRoot, Action<bool, string> check)
    {
        var skillDirectory = Path.Combine(temporaryRoot, "cache-runtime", "skills", "cache-validation");
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"),
            "---\nname: cache-validation\ndescription: Offline cache prefix validation.\n---\n# Test skill\n");
        await using var services = new WorkbenchService(
            Path.Combine(temporaryRoot, "cache-runtime"), Path.Combine(temporaryRoot, "cache-data"));
        await using var session = await StudioXMcpSession.CreateAsync(
            new StudioXMcpTools(services, project, new DenyStudioXMcpAuthorizer()));

        var historicalProtocol = new List<AiChatMessage> { new("user", "检查上一次工程状态") };
        for (var index = 0; index < 9; index++)
        {
            var id = $"old-{index}";
            historicalProtocol.Add(new AiChatMessage("assistant", null,
                ToolCalls: [new AiToolCall(id, "project_info", "{}")], ReasoningContent: "先看工程"));
            historicalProtocol.Add(new AiChatMessage("tool", "{}", ToolCallId: id));
        }
        historicalProtocol.Add(new AiChatMessage("assistant", "上一轮已检查工程"));
        var priorTurn = new AiAgentTurn("检查上一次工程状态", "上一轮已检查工程",
            ProtocolMessages: historicalProtocol);

        var workload = new PrefixTransport();
        var agent = new AiAgentService(workload, new AiSettings(), session);
        var reply = await agent.SendAsync(project, "继续检查工程", [priorTurn]);
        var frozenHistory = JsonSerializer.Serialize(workload.Requests[0].Messages.Skip(2)
            .Take(historicalProtocol.Count).ToArray());
        check(workload.Requests.Count == PrefixTransport.Batches + 1 &&
              workload.Requests.All(request => request.Messages.Count <= 128 &&
                  request.Messages[0].Content == workload.Requests[0].Messages[0].Content &&
                  request.Messages[1].StudioXKind == "skill-catalog" &&
                  request.Messages[1].Content == workload.Requests[0].Messages[1].Content &&
                  JsonSerializer.Serialize(request.Messages.Skip(2)
                      .Take(historicalProtocol.Count).ToArray()) == frozenHistory),
            "static system, Skill data before current prompt, and selected history remain stable through a long tool run");
        check(workload.Requests.Any(request => request.Messages.Any(message =>
                  message.StudioXKind == "checkpoint")) &&
              workload.Requests.All(request => request.Messages[0].StudioXKind is null) &&
              reply.History[^1].CompactedToolCalls > 0 &&
              reply.History[^1].ProtocolMessages!.Any(message => message.StudioXKind == "checkpoint"),
            "high/low compaction appends untrusted checkpoints after complete tool pairs, not in system head");
        var calls = PrefixTransport.Batches + 1;
        var expectedPrompt = Enumerable.Range(1, calls).Sum(round => 100L + round);
        var expectedHit = Enumerable.Range(1, calls).Sum(round => 70L + round);
        check(reply.Usage?.PromptTokens == 100 + calls &&
              reply.AggregateUsage is { } totals &&
              totals.RequestsWithUsage == calls && totals.RequestsWithCacheDetails == calls &&
              totals.PromptTokens == expectedPrompt &&
              totals.PromptCacheHitTokens == expectedHit &&
              totals.PromptCacheMissTokens == calls * 30L,
            "per-request cache usage is summed while the context meter keeps latest request usage");

        var steering = new AiAgentSteeringQueue();
        var delivered = new List<string>();
        steering.MessageDequeued += delivered.Add;
        var steeringTransport = new SteeringTransport(steering);
        var guidedAgent = new AiAgentService(steeringTransport, new AiSettings(), session);
        var guided = await guidedAgent.SendAsync(project, "原始任务", steering: steering);
        var protocol = guided.History[^1].ProtocolMessages!;
        check(delivered.SequenceEqual(["先确认工程范围", "回答前再说明验证范围"]) &&
              guided.History[^1].SteeringMessages!.SequenceEqual(delivered) &&
              protocol.Count(message => message.StudioXKind == "steering") == 2 &&
              steeringTransport.Requests.Count == 3 &&
              steeringTransport.Requests[1].Messages[^1].Content == "先确认工程范围" &&
              steeringTransport.Requests[2].Messages[^1].Content == "回答前再说明验证范围" &&
              !steering.IsAccepting && !steering.TryEnqueue("已经结束"),
            "guidance is consumed at paired checkpoints and a late final-response guidance continues the run");
        check(protocol.ToList().FindIndex(message => message.ToolCallId == "steer-call") <
              protocol.ToList().FindIndex(message => message.Content == "先确认工程范围"),
            "guidance never interrupts an assistant tool-call/result pair");
        var conversation = await services.AiConversations.CreateAsync(project);
        var saved = await services.AiConversations.SaveAsync(project,
            conversation with { Turns = guided.History });
        var restored = await services.AiConversations.LoadAsync(project, saved.Id);
        check(restored.Turns[^1].SteeringMessages!.SequenceEqual(delivered) &&
              restored.Turns[^1].ProtocolMessages!.Count(message => message.StudioXKind == "steering") == 2,
            "consumed guidance survives conversation persistence and protocol reload");

        var abandoned = new AiAgentSteeringQueue();
        var brokenAgent = new AiAgentService(new FailingTransport(abandoned), new AiSettings(), session);
        var failed = false;
        try { _ = await brokenAgent.SendAsync(project, "可取消任务", steering: abandoned); }
        catch (InvalidOperationException) { failed = true; }
        check(failed && !abandoned.IsAccepting &&
              abandoned.DrainPending().SequenceEqual(["未消费的提示仍在队列"]),
            "accepted but unconsumed guidance remains recoverable after transport failure");
    }

    private sealed class PrefixTransport : IAiAgentTransport
    {
        public const int Batches = 30;
        public List<AiChatRequest> Requests { get; } = [];

        public Task<AiChatResponse> CompleteAsync(AiSettings settings, AiChatRequest request,
            CancellationToken token = default)
        {
            Requests.Add(request);
            var round = Requests.Count;
            var usage = new AiTokenUsage(100 + round, 10, 110 + round,
                70 + round, 30);
            if (round == Batches + 1)
                return Task.FromResult(new AiChatResponse("检查完成", [], "stop", null, usage));
            var calls = Enumerable.Range(0, 5).Select(index =>
                new AiToolCall($"cache-{round}-{index}", "project_info", "{}")).ToArray();
            return Task.FromResult(new AiChatResponse(null, calls, "tool_calls", null,
                usage, "这批先检查工程信息"));
        }
    }

    private sealed class SteeringTransport(AiAgentSteeringQueue queue) : IAiAgentTransport
    {
        public List<AiChatRequest> Requests { get; } = [];

        public Task<AiChatResponse> CompleteAsync(AiSettings settings, AiChatRequest request,
            CancellationToken token = default)
        {
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                if (!queue.TryEnqueue("先确认工程范围")) throw new Exception("guidance was rejected");
                return Task.FromResult(new AiChatResponse(null,
                    [new AiToolCall("steer-call", "project_info", "{}")], "tool_calls", null));
            }
            if (Requests.Count == 2)
            {
                if (!queue.TryEnqueue("回答前再说明验证范围")) throw new Exception("guidance was rejected");
                return Task.FromResult(new AiChatResponse("中间回答", [], "stop", null));
            }
            return Task.FromResult(new AiChatResponse("最终回答", [], "stop", null));
        }
    }

    private sealed class FailingTransport(AiAgentSteeringQueue queue) : IAiAgentTransport
    {
        public Task<AiChatResponse> CompleteAsync(AiSettings settings, AiChatRequest request,
            CancellationToken token = default)
        {
            queue.TryEnqueue("未消费的提示仍在队列");
            throw new InvalidOperationException("offline transport failed");
        }
    }
}
