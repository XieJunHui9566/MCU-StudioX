using System.Text;
using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Application.Skills;
using StudioX.Foundation;

internal static class AssistantRedesignChecks
{
    public static async Task RunAsync(string project, string root, Action<bool, string> check)
    {
        foreach (var url in new[] { "https://api.anthropic.com/v1", "https://api.deepseek.com/anthropic", "https://example.com/v1/messages" })
        {
            var rejected = false;
            try
            {
                AiSettingsService.Validate(new(BaseUrl: url));
            }
            catch (StudioXException ex) when (ex.Code == "AI_PROTOCOL") { rejected = true; }
            check(rejected, "Anthropic Messages endpoint is rejected: " + url);
        }
        check(AiSettingsService.CompletionUri(new(BaseUrl: "https://example.com/v1/chat/completions")).AbsolutePath == "/v1/chat/completions",
            "full Chat Completions endpoint is not appended twice");
        check(AiSettingsService.CompletionUri(new(BaseUrl: "https://example.com/v1/chat/completions/")).AbsolutePath == "/v1/chat/completions",
            "complete endpoint tolerates a trailing slash");
        var legacyDirectory = Path.Combine(root, "legacy-settings");
        Directory.CreateDirectory(legacyDirectory);
        await File.WriteAllTextAsync(Path.Combine(legacyDirectory, "ai.json"), JsonSerializer.Serialize(new AiSettings(BaseUrl: "https://api.anthropic.com/v1"), JsonStore.Options));
        var legacy = new AiSettingsService(legacyDirectory);
        check((await legacy.LoadForEditAsync()).BaseUrl == "https://api.anthropic.com/v1",
            "unsupported legacy API configuration can still be opened for correction");

        var source = Path.Combine(root, "import-source", "style-fixture");
        Directory.CreateDirectory(Path.Combine(source, "references"));
        const string manifest = "---\nname: style-fixture\ndescription: 检查中文与代码风格。\n---\n保持源码中的字面值 \\u4E2D 和已有风格。\n";
        await File.WriteAllTextAsync(Path.Combine(source, "SKILL.md"), manifest);
        await File.WriteAllTextAsync(Path.Combine(source, "references", "rules.md"), "引用内容");
        var data = Path.Combine(root, "redesign-data");
        var skills = new AiSkillService(data);
        var imported = await skills.ImportAsync(Path.Combine(source, "SKILL.md"));
        check(imported.Name == "style-fixture" && imported.Scope == "imported" &&
            skills.ReadSkill(project, imported.Name).Content == manifest &&
            skills.ReadReference(project, imported.Name, "references/rules.md").Content == "引用内容" && !skills.IsProjectEnabled(project),
            "skill import preserves body and references without trusting project skills");
        var duplicate = false;
        try
        {
            await skills.ImportAsync(source);
        }
        catch (StudioXException ex) when (ex.Code == "SKILL_EXISTS") { duplicate = true; }
        check(duplicate && !Directory.EnumerateDirectories(Path.Combine(data, "skills"), ".import-*").Any(),
            "duplicate import cannot overwrite a skill and staging residues are removed");
        var invalid = Path.Combine(root, "invalid-skill");
        Directory.CreateDirectory(invalid);
        await File.WriteAllTextAsync(Path.Combine(invalid, "SKILL.md"), "not a skill");
        var invalidRejected = false;
        try
        {
            await skills.ImportAsync(invalid);
        }
        catch (StudioXException) { invalidRejected = true; }
        check(invalidRejected, "invalid imported skill is never published");

        await using var services = new WorkbenchService(Path.Combine(root, "redesign-runtime"), data);
        await using var session = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services, project, new DenyStudioXMcpAuthorizer()));
        var all = await session.ListToolsAsync();
        var compact = new AiToolCatalog(all);
        var oldBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(all));
        var newBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(compact.Definitions));
        Console.WriteLine($"MEASURE MCP schema bytes: {oldBytes} -> {newBytes}; tools {all.Count} -> {compact.Definitions.Count}; reduction {1.0 - (double)newBytes / oldBytes:P1} (not a token or billing estimate)");
        check(newBytes < oldBytes * 0.4 && all.Any(tool => tool.Name == "pic_download") &&
            all.Any(tool => tool.Name == "arduino_upload") && all.Any(tool => tool.Name == "mcp_server_call"),
            "compact tool catalog reduces schema payload and keeps newly completed MCP capabilities discoverable");
        foreach (var tool in all)
        {
            using var found = JsonDocument.Parse(compact.Search(tool.Name));
            check(found.RootElement.GetProperty("tools").EnumerateArray().Any(item => item.GetProperty("name").GetString() == tool.Name),
                "tool remains discoverable: " + tool.Name);
        }
        var routed = new RoutedTransport();
        var reply = await new AiAgentService(routed, new(), session).SendAsync(project, "检查技能");
        var skillResult = routed.Requests[^1].Messages.Last(message => message.Role == "tool").Content!;
        using var parsedSkill = JsonDocument.Parse(skillResult);
        check(parsedSkill.RootElement.GetProperty("content").GetString() == manifest && skillResult.Contains("中文") &&
            !skillResult.Contains("\\u4FDD", StringComparison.OrdinalIgnoreCase) &&
            reply.History[^1].ProtocolMessages!.Count(message => message.Role == "tool") == 2,
            "routed MCP calls retain tool pairs and JSON compaction is lossless for Chinese and literal backslashes");

        var queue = new AiAgentSteeringQueue();
        var interrupt = new InterruptTransport();
        var running = new AiAgentService(interrupt, new(), session).SendAsync(project, "原任务", steering: queue);
        await interrupt.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.TryEnqueue("改为只解释代码");
        var directed = await running.WaitAsync(TimeSpan.FromSeconds(5));
        check(interrupt.Interrupted && interrupt.Requests.Count == 2 &&
            interrupt.Requests[1].Messages[^1].Content == "改为只解释代码" &&
            directed.History[^1].SteeringMessages!.Single() == "改为只解释代码",
            "steering cancels an in-flight model request and resumes with the preserved user direction");

        var unstartedQueue = new AiAgentSteeringQueue();
        var unstarted = new UnstartedTransport(unstartedQueue);
        var safelyDirected = await new AiAgentService(unstarted, new(), session).SendAsync(project, "原始任务", steering: unstartedQueue);
        check(unstarted.Requests.Count == 2 && unstarted.Requests[1].Messages.All(message => message.Role != "tool" && message.ToolCalls is null) &&
            safelyDirected.History[^1].SteeringMessages!.Single() == "不要执行工具",
            "new direction discards proposed tool calls before they begin");

        await CustomMcpChecks.RunAsync(services, session, check);
    }

    private sealed class RoutedTransport : IAiAgentTransport
    {
        public List<AiChatRequest> Requests { get; } = [];
        public Task<AiChatResponse> CompleteAsync(AiSettings settings, AiChatRequest request, CancellationToken token = default)
        {
            Requests.Add(request);
            return Task.FromResult(Requests.Count switch
            {
                1 => new AiChatResponse(null, [new("discover", AiToolCatalog.SearchName, """{"query":"skill_read"}""")], "tool_calls", null),
                2 => new AiChatResponse(null, [new("read-style", AiToolCatalog.CallName, """{"name":"skill_read","arguments":{"name":"style-fixture"}}""")], "tool_calls", null),
                _ => new AiChatResponse("已检查", [], "stop", null)
            });
        }
    }
    private sealed class InterruptTransport : IAiAgentTransport
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<AiChatRequest> Requests { get; } = [];
        public bool Interrupted
        {
            get; private set;
        }
        public async Task<AiChatResponse> CompleteAsync(AiSettings settings, AiChatRequest request, CancellationToken token = default)
        {
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                Started.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) { Interrupted = true; throw; }
            }
            return new("已按新方向回答", [], "stop", null);
        }
    }

    private sealed class UnstartedTransport(AiAgentSteeringQueue queue) : IAiAgentTransport
    {
        public List<AiChatRequest> Requests { get; } = [];
        public Task<AiChatResponse> CompleteAsync(AiSettings settings, AiChatRequest request, CancellationToken token = default)
        {
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                queue.TryEnqueue("不要执行工具");
                return Task.FromResult(new AiChatResponse(null, [new("unstarted", "project_info", "{}")], "tool_calls", null));
            }
            return Task.FromResult(new AiChatResponse("已按要求停止工具", [], "stop", null));
        }
    }
}
