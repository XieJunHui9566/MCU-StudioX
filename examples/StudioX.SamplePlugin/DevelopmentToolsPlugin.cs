namespace StudioX.SamplePlugin;

using System.Text.Json;
using StudioX.Extensions.Abstractions;

/// <summary>API 3 示例：设置、生命周期事件、.sxdemo 补全和只读调试快照视图。</summary>
public sealed class DevelopmentToolsPlugin : IStudioXPlugin
{
    private IPluginHost? host;
    private int events;
    private string greeting = "Hello StudioX";
    private bool showDetails = true;
    public PluginContribution Describe() => new([new("status", "开发扩展示例状态", "tools")], [], [])
    {
        Settings = [new("greeting", "问候语", "string", JsonSerializer.SerializeToElement("Hello StudioX"), "显示在补全说明及状态中。"), new("showDetails", "显示详细说明", "boolean", JsonSerializer.SerializeToElement(true))],
        Events = ["project.opened", "project.closing", "document.opened", "document.changed", "document.saved", "document.closed", "settings.changed"],
        Languages = [new("demo", "StudioX 示例语言", [".sxdemo"])],
        DebugAdapters = [new("snapshot", "通用调试快照概览")]
    };
    public Task ActivateAsync(IPluginHost pluginHost, CancellationToken cancellationToken)
    {
        host = pluginHost;
        return Task.CompletedTask;
    }
    public Task DeactivateAsync(CancellationToken cancellationToken)
    {
        host = null;
        return Task.CompletedTask;
    }
    public async Task<JsonElement> InvokeAsync(string kind, string id, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (kind == "event")
        {
            events++;
            if (id == "settings.changed" && arguments.TryGetProperty("greeting", out var text))
            {
                greeting = text.GetString()!;
            }
            if (id == "settings.changed" && arguments.TryGetProperty("showDetails", out var detail))
            {
                showDetails = detail.GetBoolean();
            }
            return JsonSerializer.SerializeToElement(new
            {
                events
            });
        }
        if (kind == "language" && id == "demo")
        {
            return JsonSerializer.SerializeToElement(new[] { new PluginCompletion("print", "print(\"Hello StudioX\")", showDetails ? greeting : ""), new PluginCompletion("delay_ms", "delay_ms(100)", showDetails ? "示例延迟指令" : "") }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        if (kind == "debugAdapter" && id == "snapshot")
        {
            cancellationToken.ThrowIfCancellationRequested();
            return JsonSerializer.SerializeToElement(DebugSnapshotPanel.Create(arguments, showDetails), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        if (kind == "command" && id == "status")
        {
            if (host is not null)
            {
                await host.LogAsync("info", $"{greeting}；已收到 {events} 个事件。", cancellationToken);
            }
            return JsonSerializer.SerializeToElement(new
            {
                greeting,
                events
            });
        }
        throw new InvalidOperationException("不受支持的调用：" + kind + "/" + id);
    }
}
