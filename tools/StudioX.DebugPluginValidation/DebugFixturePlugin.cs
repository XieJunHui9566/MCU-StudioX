namespace StudioX.DebugPluginValidation;

using System.Text.Json;
using StudioX.Extensions.Abstractions;

/// <summary>真实独立宿主夹具：可暂停响应、返回错误和非法控件，以验证迟到结果撤销。</summary>
public sealed class DebugFixturePlugin : IStudioXPlugin
{
    private IPluginHost? host;
    public PluginContribution Describe() => new([], [], []) { DebugAdapters = [new("snapshot", "Debug fixture")] };
    public Task ActivateAsync(IPluginHost host, CancellationToken token)
    {
        this.host = host;
        return Task.CompletedTask;
    }
    public Task DeactivateAsync(CancellationToken token)
    {
        host = null;
        return Task.CompletedTask;
    }
    public async Task<JsonElement> InvokeAsync(string kind, string id, JsonElement input, CancellationToken token)
    {
        var reply = await host!.CallAsync("validation.wait", input, token);
        var mode = reply.GetProperty("mode").GetString();
        if (mode == "error")
        {
            throw new InvalidOperationException("debug-fixture-original-error");
        }
        if (mode == "unknown")
        {
            return JsonSerializer.SerializeToElement(new
            {
                id = "snapshot",
                title = "Invalid",
                widgets = Array.Empty<object>(),
                xaml = "execute"
            });
        }
        var widget = mode == "action"
            ? new PluginPanelWidget("invalid", "button", "Cannot execute", CommandId: "write")
            : new PluginPanelWidget("revision", "text", "Revision", JsonSerializer.SerializeToElement(input.GetProperty("revision").GetInt64().ToString()));
        return JsonSerializer.SerializeToElement(new PluginPanelDefinition("snapshot", "Fixture", [widget]), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
