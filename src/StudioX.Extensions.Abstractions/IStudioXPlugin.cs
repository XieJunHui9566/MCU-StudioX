namespace StudioX.Extensions.Abstractions;

using System.Text.Json;

/// <summary>用户插件的生命周期与贡献入口；实例仅在独立插件进程内创建。</summary>
public interface IStudioXPlugin
{
    PluginContribution Describe();

    Task ActivateAsync(IPluginHost host, CancellationToken cancellationToken);

    Task<JsonElement> InvokeAsync(string kind, string id, JsonElement arguments, CancellationToken cancellationToken);

    Task DeactivateAsync(CancellationToken cancellationToken);
}
