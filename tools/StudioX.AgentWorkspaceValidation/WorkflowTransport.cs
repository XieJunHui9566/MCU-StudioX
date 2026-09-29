using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Editing;
using StudioX.Foundation;

/// <summary>替换模型而不替换 MCP、clangd 或构建器，复现修改后验证失败再修复的工具链。</summary>
internal sealed class WorkflowTransport : IAiAgentTransport
{
    private int round;
    public bool SawBuildFailure
    {
        get; private set;
    }
    public bool SawBuildSuccess
    {
        get; private set;
    }
    public bool SawEditorInstructions
    {
        get; private set;
    }
    public Task<AiChatResponse> CompleteAsync(AiSettings settings, AiChatRequest request, CancellationToken token = default)
    {
        SawEditorInstructions |= request.Messages.Any(m => m.Role == "system" && m.Content?.Contains("editor_plan_changes", StringComparison.Ordinal) == true);
        JsonElement Last()
        {
            var text = request.Messages.Last(m => m.Role == "tool").Content!;
            Console.WriteLine("WORKFLOW " + round + " " + text);
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        AiChatResponse Call(string name, object args) => new(null, [new("workflow-" + round, name, JsonSerializer.Serialize(args, JsonStore.Options))], "tool_calls", "offline-scripted");
        var next = ++round switch
        {
            1 => Call("editor_read", new { path = "src/main.c" }),
            2 => Call("editor_plan_changes", new { files = new[] { new AgentTextPatch("src/main.c", Last().GetProperty("contentHash").GetString()!, [new("helper();", "helper()")]) }, reason = "模拟模型引入缺分号，检验验证修复流程" }),
            3 => Call("editor_apply_plan", new { planId = Last().GetProperty("id").GetString() }),
            4 => Call("project_build", new { }),
            5 => Fix(Last()),
            6 => Call("editor_apply_plan", new { planId = Last().GetProperty("id").GetString() }),
            7 => Call("project_build", new { }),
            8 => CompleteBuild(Last()),
            _ => new AiChatResponse("编译通过；未做实板验证。", [], "stop", "offline-scripted")
        };
        return Task.FromResult(next);
        AiChatResponse Fix(JsonElement result)
        {
            SawBuildFailure = !result.GetProperty("Success").GetBoolean();
            return Call("editor_plan_refactor", new
            {
                path = "src/main.c",
                action = "fix",
                offset = 80
            });
        }
        AiChatResponse CompleteBuild(JsonElement result)
        {
            SawBuildSuccess = result.GetProperty("Success").GetBoolean() && !result.GetProperty("stale").GetBoolean();
            return Call("editor_task_status", new
            {
            });
        }
    }
}
