namespace StudioX.Extensions.Abstractions;

using System.Text.Json;

/// <summary>插件通过受应用权限检查的桥接调用工具，并发布纯数据面板及日志。</summary>
public interface IPluginHost
{
    /// <summary>调用清单中声明的宿主工具，保留应用服务的权限、路径与设备会话检查。</summary>
    /// <exception cref="PluginHostException">宿主拒绝请求、工具不可用或调用失败；Code 保留协议错误码，Message 保留原始诊断。</exception>
    /// <exception cref="OperationCanceledException">调用者或插件会话已取消。</exception>
    Task<JsonElement> CallAsync(string tool, JsonElement arguments, CancellationToken cancellationToken);

    Task PublishPanelAsync(PluginPanelDefinition panel, CancellationToken cancellationToken);

    Task LogAsync(string level, string message, CancellationToken cancellationToken);
}
