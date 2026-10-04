namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>隔离用户目录中验证真实插件宿主与离线 MI；本入口不启动任何硬件会话。</summary>
    public async Task RenderDebugPluginsPreviewAsync(string directory, string packArchive, string pluginArchive)
    {
        var checks = new List<string>();
        void Check(bool passed, string message)
        {
            if (!passed)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await debugNavigationTask;
            UpdateLayout();
        }
        async Task WaitAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!condition())
            {
                await Task.Delay(20, timeout.Token);
            }
            await Settle();
        }
        var installed = await services.PluginManager.ImportAsync(pluginArchive);
        await services.PluginManager.SetEnabledAsync(installed.Id, true);
        await services.Packs.ImportAsync(packArchive);
        var root = await DebugSessionService.CreateExampleAsync(services.Packs, services.DataDirectory, Path.Combine(AppContext.BaseDirectory, "device-packs"));
        await OpenProjectAsync(root, CancellationToken.None);
        await PluginManager.RefreshAsync();
        ShowDocument(ExtensionsTab);
        await Settle();
        var row = PluginManager.CatalogList.Items.Cast<PluginCatalogRow>().Single();
        Check(row.CanDebug && row.CanSettings && row.Description.Length > 0, "运行贡献提供设置和调试扩展直接入口及说明");
        PluginManager.CatalogFilter.Text = "不存在的插件";
        Check(PluginManager.CatalogList.Items.Count == 0 && PluginManager.EmptyHint.Visibility == Visibility.Visible, "插件搜索无结果有提示");
        PluginManager.CatalogFilter.Text = "调试快照";
        Check(PluginManager.CatalogList.Items.Count == 1, "可按中文能力搜索插件");
        PluginManager.CapabilityFilter.SelectedIndex = 7;
        Check(PluginManager.CatalogList.Items.Count == 0, "按未声明的数据解码能力筛选");
        PluginManager.CapabilityFilter.SelectedIndex = 1;
        Check(PluginManager.CatalogList.Items.Count == 1, "按调试快照能力筛选");
        PluginManager.CatalogFilter.Text = "";
        await Settle();
        Render(this, Path.Combine(directory, "plugin-manager.png"));
        await PluginManager.ShowDebugRequestedAsync!(installed.Id);
        var item = pluginDebugViews.Values.Single();
        Check(item.View.StateText.Text.Contains("未连接") && item.View.PanelHost.Content is null && !item.View.RefreshButton.IsEnabled, "管理页打开只读标签，不自动连接调试");
        await PluginManager.ShowDebugRequestedAsync!(installed.Id);
        Check(pluginDebugViews.Count == 1, "同一调试扩展复用标签");
        await services.Debugger.StartOfflineAsync();
        await WaitAsync(() => item.Session.Current.Panel is not null && item.View.PanelHost.Content is not null);
        Check(item.View.StateText.Text.Contains("离线模拟") && item.View.RefreshButton.IsEnabled, "暂停后自动显示快照并标明模拟来源");
        Check(item.Session.Current.Panel!.Widgets.Any(w => w.Id == "registers" && w.Value?.GetArrayLength() == 56) &&
            item.Session.Current.Panel.Widgets.Any(w => w.Id == "watches"), "开发示例呈现寄存器和观察项表格");
        ShowDocument(item.Tab);
        await Settle();
        Render(this, Path.Combine(directory, "debug-plugin-stopped.png"));
        var revision = item.Session.Current.Revision;
        await services.Debugger.ExecuteAsync(DebugAction.StepOver);
        await WaitAsync(() => item.Session.Current.Panel is not null && item.Session.Current.Revision > revision);
        Check(item.Session.Current.Panel!.Widgets.Single(w => w.Id == "frames").Value?.GetRawText().Contains("main") == true, "单步后刷新调用栈表格");
        await services.Debugger.ExecuteAsync(DebugAction.Continue);
        await Settle();
        Check(item.View.PanelHost.Content is null && item.View.StateText.Text.Contains("运行中") && !item.View.RefreshButton.IsEnabled, "继续运行清除显示结果和刷新入口");
        Render(this, Path.Combine(directory, "debug-plugin-running.png"));
        await services.Debugger.ExecuteAsync(DebugAction.Pause);
        await WaitAsync(() => item.Session.Current.Panel is not null);
        await CloseWorkspaceTabAsync(item.Tab);
        Check(pluginDebugViews.Count == 0 && !WorkspaceTabs.Items.Contains(item.Tab) && item.Session.Current.Panel is null, "关闭标签释放订阅、数据和后台解释");
        await PluginManager.ShowDebugRequestedAsync!(installed.Id);
        item = pluginDebugViews.Values.Single();
        await WaitAsync(() => item.Session.Current.Panel is not null);
        await services.PluginManager.SetEnabledAsync(installed.Id, false);
        await WaitAsync(() => item.View.PanelHost.Content is null && item.View.StateText.Text.Contains("停止"));
        RefreshPluginContributionActions();
        row = PluginManager.CatalogList.Items.Cast<PluginCatalogRow>().Single();
        Check(!row.CanDebug && !row.CanSettings, "停用后撤销入口和当前标签内容");
        await ReloadPluginWorkspaceAsync(CancellationToken.None);
        Check(pluginDebugViews.Count == 0 && !WorkspaceTabs.Items.Contains(item.Tab), "重新加载工作区移除已撤销的调试扩展标签");
        await services.Debugger.StopAsync();
        await JsonStore.WriteAsync(Path.Combine(directory, "result.json"), new
        {
            success = true,
            hardware = false,
            checks
        });
    }
}
