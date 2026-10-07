namespace StudioX.Desktop;

public partial class MainWindow
{
    // 应用会话独立于工程切换；创建新工程的入口不应要求先打开一个无关工程。
    private async Task EnsureApplicationPluginsAsync(CancellationToken token)
    {
        if (pluginApplication is not null || closing || closed)
        {
            return;
        }
        var session = await services.PluginManager.OpenApplicationSessionAsync(token);
        if (closing || closed || token.IsCancellationRequested)
        {
            await session.DisposeAsync();
            token.ThrowIfCancellationRequested();
            return;
        }
        pluginApplicationCancellation = new CancellationTokenSource();
        pluginApplication = session;
        session.Changed += PluginWorkspace_Changed;
        foreach (var active in session.Contributions)
        {
            pluginStopped.Remove(active.Id);
        }
        RebuildPluginCommandUi();
        foreach (var (key, panel) in session.LatestPanels)
        {
            UpdatePluginPanel(key, panel);
        }
        foreach (var diagnostic in session.Diagnostics)
        {
            PluginManager.Log(diagnostic);
        }
        PluginManager.WorkspaceHint.Text = session.Contributions.Count > 0
            ? "应用级插件已加载，无需打开工程。" : "应用级插件在启动时加载；工程插件在打开工程后加载。";
    }

    private async Task ReloadPluginSessionsAsync(CancellationToken token)
    {
        // 安装、信任状态或内容改变后重新建立会话，不能保留旧包的授权。
        await StopApplicationPluginsAsync();
        await ReloadPluginWorkspaceAsync(token);
    }

    private async Task StopApplicationPluginsAsync()
    {
        await pluginWorkspaceGate.WaitAsync();
        try
        {
            var session = pluginApplication;
            if (session is null)
            {
                return;
            }
            pluginApplication = null;
            pluginApplicationCancellation?.Cancel();
            var ownsInvocation = ReferenceEquals(pluginInvocationSession, session);
            if (ownsInvocation)
            {
                pluginInvocationCancellation?.Cancel();
            }
            session.Changed -= PluginWorkspace_Changed;
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception error) { PluginManager.Log(error.ToString()); }
            if (ownsInvocation)
            {
                try
                {
                    await pluginInvocationTask;
                }
                catch (Exception error) { PluginManager.Log(error.ToString()); }
            }
            pluginApplicationCancellation?.Dispose();
            pluginApplicationCancellation = null;
            RemovePluginSessionUi(session);
            RebuildPluginCommandUi();
        }
        finally { pluginWorkspaceGate.Release(); }
    }
}
