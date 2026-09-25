using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Foundation;

internal static class AgentExternalWorkloadChecks
{
    public static async Task RunAsync(StudioXMcpSession session, SwitchingAuthorizer authorizer,
        string project, string temporaryRoot, Action<bool, string> check)
    {
        var example = Path.Combine(temporaryRoot, "agent-external-example");
        Directory.CreateDirectory(example);
        await File.WriteAllTextAsync(Path.Combine(example, "example.c"), "int example(void) { return 1; }\n");

        var previousApproval = authorizer.Allow;
        var previousRequests = authorizer.Requests.Count;
        try
        {
            authorizer.Allow = true;
            var transport = new ExternalBurstTransport(example);
            var agent = new AiAgentService(transport, new AiSettings(), session);
            var reply = await agent.SendAsync(project, "浏览外部示例并确定需要复制的文件");
            var openRequests = authorizer.Requests.Skip(previousRequests)
                .Where(request => request.Tool == "external_project_open").ToArray();
            check(openRequests.Length == 1 &&
                  openRequests[0].Permission == StudioXMcpPermission.ExternalRead &&
                  transport.RootId is { Length: > 0 },
                "Agent receives one session-scoped external directory grant before browsing");
            check(transport.Requests.Count >= 9 &&
                  transport.Requests.Skip(1).Any(request => request.Messages.Any(message =>
                      message.Role == "tool" && message.Content?.Contains("example.c", StringComparison.Ordinal) == true)) &&
                  reply.History.Single().ProtocolMessages?.Count(message => message.Role == "tool") >= 34,
                "Agent retains approved external browsing results across many tool rounds");
            check(transport.Requests.Count == 10 &&
                  reply.History.Single().ProtocolMessages?.Count(message => message.Role == "tool") == 37 &&
                  reply.Text.Contains("示例已读取", StringComparison.Ordinal),
                "Agent executes the three calls after 34 completed calls and returns the model's answer");

            // 重复读取 24 KiB 源码，验证轮内超过旧工具额度后仍能跨过消息数和请求体边界。
            var workloadFile = Path.Combine(project, "src", "agent-workload.c");
            await File.WriteAllTextAsync(workloadFile,
                "/* agent workload fixture\n" + new string('x', 24_000) + "\n*/\n");
            var workloadTransport = new LongWorkloadTransport();
            var workloadProgress = new InlineProgress();
            var workloadAgent = new AiAgentService(workloadTransport, new AiSettings(), session);
            var workloadReply = await workloadAgent.SendAsync(project,
                "读取并分析较长工程中的源码，完成后给出结论", progress: workloadProgress);
            check(workloadProgress.CompletedTools == LongWorkloadTransport.TotalCalls &&
                  workloadTransport.Requests.Count >= LongWorkloadTransport.Batches + 1 &&
                  workloadReply.Text == "已完成长工程分析。" &&
                  !workloadReply.Text.Contains("上限", StringComparison.Ordinal),
                "Agent completes 180 actual MCP calls in one request without a fixed tool quota");
            check(workloadTransport.Requests.All(ValidProviderRequest) &&
                  workloadTransport.Requests.Count > 13 &&
                  !workloadTransport.Requests.SelectMany(request => request.Messages)
                      .Where(message => message.Role == "tool")
                      .Any(message => message.Content?.StartsWith("{\"error\"", StringComparison.OrdinalIgnoreCase) == true) &&
                  workloadTransport.Requests[^1].Messages.Any(message =>
                      message.Role == "assistant" &&
                      message.ReasoningContent == LongWorkloadTransport.Reasoning &&
                      message.ToolCalls?.Any(call => call.Id == "long-read-18-0") == true) &&
                  workloadTransport.Requests[^1].Messages.Any(message =>
                      message.Role == "tool" &&
                      message.ToolCallId == "long-read-18-0" &&
                      message.Content?.Contains("agent workload fixture", StringComparison.Ordinal) == true &&
                      !message.Content.Contains("error", StringComparison.OrdinalIgnoreCase)),
                "long-running Agent requests stay within provider bounds and keep valid tool-call ordering");
            check(workloadReply.History.Single().Assistant == workloadReply.Text &&
                  workloadReply.History.Single().CompactedToolCalls > 0 &&
                  !string.IsNullOrWhiteSpace(workloadReply.History.Single().ContextSummary) &&
                  workloadReply.History.Single().ProtocolMessages is { } workloadProtocol &&
                  workloadReply.History.Single().CompactedToolCalls +
                      workloadProtocol.Count(message => message.Role == "tool") == LongWorkloadTransport.TotalCalls &&
                  workloadProtocol[0].Role == "user" && workloadProtocol[^1].Role == "assistant" &&
                  ValidProtocol(workloadProtocol),
                "completed long workload checkpoints older calls and retains a valid, resumable recent protocol");
            var historyStore = new AiConversationStore(Path.Combine(temporaryRoot, "agent-long-history"));
            var conversation = await historyStore.CreateAsync(project);
            var saved = await historyStore.SaveAsync(project,
                conversation with { Title = "长工程分析", Turns = workloadReply.History });
            var loaded = await historyStore.LoadAsync(project, saved.Id);
            check(loaded.Turns.Single().Assistant == workloadReply.Text &&
                  loaded.Turns.Single().ProtocolMessages is { } savedProtocol &&
                  ValidProtocol(savedProtocol),
                "long workload history can be persisted and loaded within the conversation file bound");

            var resumedTransport = new ResumedHistoryTransport();
            var resumedAgent = new AiAgentService(resumedTransport, new AiSettings(), session);
            var resumedReply = await resumedAgent.SendAsync(project, "继续分析", loaded.Turns);
            check(resumedReply.Text == "后续分析完成。" &&
                  resumedTransport.Requests.Count == 1 &&
                  ValidProviderRequest(resumedTransport.Requests[0]) &&
                  resumedTransport.Requests[0].Messages.Any(message =>
                      message.Role == "assistant" && message.Content == workloadReply.Text),
                "long workload history can be reused without exceeding the next request limits");

            using var cancellation = new CancellationTokenSource();
            var cancellationTransport = new LongWorkloadTransport();
            var cancellationProgress = new InlineProgress(completed =>
            {
                if (completed == 105) cancellation.Cancel();
            });
            var cancellationAgent = new AiAgentService(cancellationTransport, new AiSettings(), session);
            var stopped = false;
            try
            {
                _ = await cancellationAgent.SendAsync(project, "继续大量读取源码",
                    token: cancellation.Token, progress: cancellationProgress);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                stopped = true;
            }
            check(stopped && cancellationProgress.CompletedTools == 105 &&
                  cancellationTransport.Requests.Count < LongWorkloadTransport.Batches + 1,
                "user cancellation stops a long-running Agent after more than 100 tool calls");

            const string dsml = "<|DSML|calls><|DSML|invoke name=\"project_create_file\" " +
                                "path=\"src/dsml-should-not-exist.c\"/><|DSML|calls/>";
            var taintedHistory = new AiAgentTurn("创建测试文件", dsml,
                ProtocolMessages: [new AiChatMessage("user", "创建测试文件"),
                    new AiChatMessage("assistant", dsml)]);
            var historicalTransport = new ResumedHistoryTransport();
            var historicalAgent = new AiAgentService(historicalTransport, new AiSettings(), session);
            var historicalReply = await historicalAgent.SendAsync(project, "核查上一轮进度", [taintedHistory]);
            check(historicalReply.Text == "后续分析完成。" &&
                  historicalTransport.Requests.Count == 1 &&
                  historicalTransport.Requests[0].Messages.Any(message =>
                      message.Role == "assistant" &&
                      message.Content?.Contains("未执行的工具标记", StringComparison.Ordinal) == true) &&
                  historicalTransport.Requests[0].Messages.All(message =>
                      message.Content?.Contains("DSML", StringComparison.OrdinalIgnoreCase) != true),
                "unexecuted DSML markup in old assistant history is sanitized before the next model request");

            var dsmlTransport = new LiteralDsmlTransport(dsml);
            var dsmlAgent = new AiAgentService(dsmlTransport, new AiSettings(), session);
            var authorizationsBeforeDsml = authorizer.Requests.Count;
            var rejectedDsml = false;
            try
            {
                _ = await dsmlAgent.SendAsync(project, "创建测试文件");
            }
            catch (StudioXException ex) when (ex.Code == "AI_RESPONSE_FORMAT" &&
                                              ex.Message.Contains("未执行", StringComparison.Ordinal))
            {
                rejectedDsml = true;
            }
            check(rejectedDsml && dsmlTransport.Requests.Count == 1 &&
                  authorizer.Requests.Count == authorizationsBeforeDsml &&
                  !File.Exists(Path.Combine(project, "src", "dsml-should-not-exist.c")),
                "literal DSML tool markup in current model content cannot masquerade as a completed answer or tool call");

            var malformed = new AiAgentService(new MalformedToolTransport(), new AiSettings(), session);
            var malformedReply = await malformed.SendAsync(project, "检查错误的工具调用格式");
            check(malformedReply.Text.Contains("格式", StringComparison.Ordinal) &&
                  !malformedReply.Text.Contains("过多", StringComparison.Ordinal),
                "malformed tool-call ID is diagnosed separately from the workload limit");
        }
        finally
        {
            authorizer.Allow = previousApproval;
        }
    }

    private sealed class ExternalBurstTransport(string externalDirectory) : IAiAgentTransport
    {
        public List<AiChatRequest> Requests { get; } = [];
        public string? RootId { get; private set; }

        public Task<AiChatResponse> CompleteAsync(AiSettings settings, AiChatRequest request,
            CancellationToken token = default)
        {
            Requests.Add(request);
            var round = Requests.Count;
            if (round == 1)
            {
                var arguments = JsonSerializer.Serialize(new { directory = externalDirectory });
                return Task.FromResult(new AiChatResponse(null,
                    [new AiToolCall("external-open", "external_project_open", arguments)],
                    "tool_calls", null));
            }

            if (RootId is null)
            {
                var opened = request.Messages.Last(message => message.Role == "tool").Content!;
                using var result = JsonDocument.Parse(opened);
                RootId = result.RootElement.GetProperty("rootId").GetString();
            }

            if (round >= 10 || request.Tools is { Count: 0 })
                return Task.FromResult(new AiChatResponse("示例已读取，可继续选择文件复制。", [], "stop", null));

            var calls = round == 8 || round == 9 ? 3 : 5;
            var argumentsJson = JsonSerializer.Serialize(new { rootId = RootId, directory = "" });
            var batch = Enumerable.Range(0, calls).Select(index =>
                new AiToolCall($"external-list-{round}-{index}",
                    "external_project_list_files", argumentsJson)).ToArray();
            return Task.FromResult(new AiChatResponse(null, batch, "tool_calls", null));
        }
    }

    private sealed class LongWorkloadTransport : IAiAgentTransport
    {
        public const int Batches = 18;
        public const int CallsPerBatch = 10;
        public const int TotalCalls = Batches * CallsPerBatch;
        public const string Reasoning = "读取本批源码后继续分析。";
        public List<AiChatRequest> Requests { get; } = [];

        public Task<AiChatResponse> CompleteAsync(AiSettings settings, AiChatRequest request,
            CancellationToken token = default)
        {
            Requests.Add(request);
            if (Requests.Count == Batches + 1)
                return Task.FromResult(new AiChatResponse("已完成长工程分析。", [], "stop", null));
            if (Requests.Count > Batches + 1)
                throw new Exception("Agent did not stop after the model's final answer");
            var argumentsJson = JsonSerializer.Serialize(new { path = "src/agent-workload.c" });
            var calls = Enumerable.Range(0, CallsPerBatch).Select(index =>
                new AiToolCall($"long-read-{Requests.Count}-{index}",
                    "project_read_file", argumentsJson)).ToArray();
            return Task.FromResult(new AiChatResponse(null, calls, "tool_calls", null,
                ReasoningContent: Reasoning));
        }
    }

    private sealed class ResumedHistoryTransport : IAiAgentTransport
    {
        public List<AiChatRequest> Requests { get; } = [];

        public Task<AiChatResponse> CompleteAsync(AiSettings settings, AiChatRequest request,
            CancellationToken token = default)
        {
            Requests.Add(request);
            if (Requests.Count > 1)
                throw new Exception("Unexpected extra model request in resumed history test");
            return Task.FromResult(new AiChatResponse("后续分析完成。", [], "stop", null));
        }
    }

    private sealed class LiteralDsmlTransport(string response) : IAiAgentTransport
    {
        public List<AiChatRequest> Requests { get; } = [];

        public Task<AiChatResponse> CompleteAsync(AiSettings settings, AiChatRequest request,
            CancellationToken token = default)
        {
            Requests.Add(request);
            return Task.FromResult(new AiChatResponse(response, [], "stop", null));
        }
    }

    private sealed class InlineProgress(Action<int>? afterCompletion = null) : IProgress<AiAgentProgress>
    {
        public int CompletedTools { get; private set; }

        public void Report(AiAgentProgress value)
        {
            if (value.Kind != AiAgentProgressKind.ToolCallCompleted) return;
            CompletedTools++;
            afterCompletion?.Invoke(CompletedTools);
        }
    }

    private static bool ValidProviderRequest(AiChatRequest request)
    {
        if (request.Messages.Count is < 2 or > 128 ||
            request.Messages[0].Role != "system" ||
            request.Tools is not { Count: > 0 } ||
            JsonSerializer.SerializeToUtf8Bytes(request).Length > 2 * 1024 * 1024)
            return false;
        return ValidProtocol(request.Messages.Skip(1).ToArray());
    }

    private static bool ValidProtocol(IReadOnlyList<AiChatMessage> messages)
    {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role == "assistant" && message.ToolCalls is { Count: > 0 } calls)
            {
                if (pending.Count > 0) return false;
                foreach (var call in calls)
                    if (!pending.Add(call.Id)) return false;
            }
            else if (message.Role == "tool")
            {
                if (message.ToolCallId is null || !pending.Remove(message.ToolCallId)) return false;
            }
            else if (pending.Count > 0) return false;
        }
        return pending.Count == 0;
    }

    private sealed class MalformedToolTransport : IAiAgentTransport
    {
        public Task<AiChatResponse> CompleteAsync(AiSettings settings, AiChatRequest request,
            CancellationToken token = default) => Task.FromResult(new AiChatResponse(null,
                [new AiToolCall("", "external_project_list_files", "{}")], "tool_calls", null));
    }
}
