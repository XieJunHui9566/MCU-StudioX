namespace StudioX.Extensions.Abstractions;

using System.Text.Json;

/// <summary>插件通过受应用权限检查的桥接调用工具，并发布纯数据面板及日志。</summary>
public interface IPluginHost
{
    Task<JsonElement> CallAsync(string tool, JsonElement arguments, CancellationToken cancellationToken);

    Task PublishPanelAsync(PluginPanelDefinition panel, CancellationToken cancellationToken);

    Task LogAsync(string level, string message, CancellationToken cancellationToken);
}
