namespace StudioX.PluginRuntimeValidation;

using System.Text.Json;
using StudioX.Extensions.Abstractions;

/// <summary>非法声明夹具；若错误进入激活，立即调用宿主以便门禁观察。</summary>
public sealed class InvalidContributionPlugin : IStudioXPlugin
{
    public PluginContribution Describe()
    {
        return new([], [new PluginPanelDefinition("invalid", "Invalid", [new PluginPanelWidget("xaml", "xaml", "Executable UI")])], []);
    }

    public async Task ActivateAsync(IPluginHost host, CancellationToken cancellationToken)
    {
        _ = await host.CallAsync("validation.activation", JsonSerializer.SerializeToElement(new { }), cancellationToken);
    }

    public Task<JsonElement> InvokeAsync(string kind, string id, JsonElement arguments, CancellationToken cancellationToken)
    {
        return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
    }

    public Task DeactivateAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
