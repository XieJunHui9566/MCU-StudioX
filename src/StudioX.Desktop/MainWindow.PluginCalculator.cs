namespace StudioX.Desktop;

using System.Text.Json;

public partial class MainWindow
{
    private async Task<JsonElement> InvokeCalculatorPanelAsync(string pluginId, string command, JsonElement arguments)
    {
        if (!CanRunPlugin(pluginId) || FindPluginSession(pluginId) is not { } session)
        {
            throw new InvalidOperationException("计算器插件当前不可用。");
        }
        var contribution = session.Contributions.Single(active => active.Id == pluginId);
        if (contribution.Manifest.HostTools is { Length: > 0 })
        {
            await InvokePluginCommandAsync(pluginId, command, arguments);
            return default;
        }
        var lifetime = ReferenceEquals(session, pluginApplication) ? pluginApplicationCancellation : pluginWorkspaceCancellation;
        if (lifetime is null)
        {
            throw new InvalidOperationException("计算器会话已结束。");
        }
        // 无宿主工具权限的数据计算无需激活窗口和刷新全局命令；仍走声明校验、隔离进程和原会话取消。
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        return await session.InvokeAsync(pluginId, "command", command, arguments, deadline.Token);
    }
}
