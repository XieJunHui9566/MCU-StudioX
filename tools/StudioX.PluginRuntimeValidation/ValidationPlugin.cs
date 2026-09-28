namespace StudioX.PluginRuntimeValidation;

using System.Text.Json;
using StudioX.Extensions.Abstractions;

/// <summary>隔离验证用的真实 SDK 插件，覆盖状态、双向回调、取消及进程故障。</summary>
public sealed class ValidationPlugin : IStudioXPlugin
{
    private IPluginHost? host;
    private int count;

    public PluginContribution Describe()
    {
        return new PluginContribution(
            new[] { "increment", "callback", "throw", "cancel", "crash", "environment", "reenter", "write" }
                .Select(id => new PluginCommandDefinition(id, id)).ToArray(),
            [new PluginPanelDefinition("status", "Validation", [new PluginPanelWidget("count", "text", "Count", JsonSerializer.SerializeToElement("0"))])],
            [new PluginAgentToolDefinition("counter", "验证持久插件状态。", JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false }))]);
    }

    public async Task ActivateAsync(IPluginHost host, CancellationToken cancellationToken)
    {
        this.host = host;
        Console.WriteLine("fixture Console output redirected to stderr");
        await host.LogAsync("info", "activated", cancellationToken);
        await host.PublishPanelAsync(new PluginPanelDefinition("status", "Validation",
            [new PluginPanelWidget("count", "text", "Count", JsonSerializer.SerializeToElement(count.ToString(System.Globalization.CultureInfo.InvariantCulture)))]), cancellationToken);
    }

    public async Task<JsonElement> InvokeAsync(string kind, string id, JsonElement arguments, CancellationToken cancellationToken)
    {
        switch (id)
        {
            case "increment":
            case "counter":
                return JsonSerializer.SerializeToElement(new { count = ++count });
            case "callback":
                return await host!.CallAsync("validation.echo", arguments, cancellationToken);
            case "reenter":
                return await host!.CallAsync("validation.reenter", arguments, cancellationToken);
            case "write":
                return await host!.CallAsync("project_edit_file", arguments, cancellationToken);
            case "throw":
                throw new InvalidOperationException("validation-original-error");
            case "cancel":
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                return JsonSerializer.SerializeToElement(new { cancelled = false });
            case "environment":
                return JsonSerializer.SerializeToElement(new { secret = Environment.GetEnvironmentVariable("STUDIOX_VALIDATION_SECRET") });
            case "crash":
                Environment.Exit(37);
                return JsonSerializer.SerializeToElement(new { });
            default:
                throw new InvalidOperationException($"未知夹具调用：{kind}/{id}");
        }
    }

    public Task DeactivateAsync(CancellationToken cancellationToken)
    {
        host = null;
        return Task.CompletedTask;
    }
}
