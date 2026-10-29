namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using StudioX.Engine;

public partial class MainWindow
{
    private ProjectClockState? loadedProjectClock;
    private bool clockProviderEnabled;
    private bool clockRefreshQueued;
    private bool clockRefreshAgain;

    private void QueueManagedClockRefresh()
    {
        if (!IsStcSdccProject || projectDirectory is not { } root)
        {
            return;
        }
        if (clockRefreshQueued)
        {
            clockRefreshAgain = true;
            return;
        }
        clockRefreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(async () =>
        {
            try
            {
                var clock = await services.ProjectClocks.ReadAsync(root);
                var enabled = services.ProjectClocks.Ownership is not null && await services.ProjectClocks.Ownership.RequiresManagedClockAsync(root);
                var settings = await services.StcIsp.LoadSettingsAsync(root);
                if (closing || closed || projectDirectory != root)
                {
                    return;
                }
                loadedProjectClock = clock;
                clockProviderEnabled = enabled;
                if (loadedStcIspSettings is { } previous)
                {
                    loadedStcIspSettings = previous with
                    {
                        ClockMode = settings.ClockMode,
                        ClockFrequencyHz = settings.ClockFrequencyHz
                    };
                    applyingStcIsp = true;
                    try
                    {
                        StcClockModePicker.SelectedValue = settings.ClockMode;
                        StcClockFrequencyBox.Text = settings.ClockFrequencyHz is { } hz ? (hz / 1000000m).ToString("0.######", CultureInfo.InvariantCulture) : "";
                    }
                    finally { applyingStcIsp = false; }
                }
                UpdateStcClockHelp();
            }
            catch (Exception error) { Log("刷新图形时钟：" + error); }
            finally
            {
                clockRefreshQueued = false;
                if (clockRefreshAgain)
                {
                    clockRefreshAgain = false;
                    QueueManagedClockRefresh();
                }
            }
        }));
    }

    private void OpenManagedClock_Click(object sender, RoutedEventArgs e)
    {
        if (loadedProjectClock?.PluginId is { } id && pluginActivities.TryGetValue(id, out var activity))
        {
            ShowPluginActivity(id, activity.Tab);
            foreach (var panel in pluginPanels.Where(item => item.Key.StartsWith(id + "/", StringComparison.Ordinal)))
            {
                foreach (var view in panel.Value.Renderer.ConfigurationViews)
                {
                    view.SelectPage(1);
                }
            }
        }
        else
        {
            ShowDocument(ExtensionsTab);
            Status.Text = "请启用与当前工程匹配的图形配置插件，或明确解除托管后使用手动时钟。";
        }
    }

    private async void ReleaseManagedClock_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        EnsureNoActiveDebug();
        await services.GraphicalClockControl.ReleaseAsync(RequireProject(), token);
        await ResynchronizeProjectFilesAsync();
        QueueManagedClockRefresh();
        Status.Text = "已解除时钟托管；手动时钟从“保留当前来源”开始，请明确选择并保存，不恢复旧频率。";
    });
}
