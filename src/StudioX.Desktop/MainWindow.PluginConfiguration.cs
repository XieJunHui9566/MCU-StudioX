namespace StudioX.Desktop;

using System.Windows;
using StudioX.Application;

public partial class MainWindow
{
    private readonly Dictionary<string, string> discardedConfigurationDrafts = new(StringComparer.Ordinal);

    private async Task<bool> ConfirmPluginConfigurationDraftsAsync(Func<SourceDocument, MessageBoxResult>? decide)
    {
        FlushPluginPanels();
        foreach (var (key, panel) in pluginPanels)
        {
            var view = panel.Renderer.ConfigurationViews.FirstOrDefault();
            if (view?.HasChanges != true)
            {
                continue;
            }
            var snapshot = pluginGeneration + ":" + view.DraftIdentity();
            if (discardedConfigurationDrafts.TryGetValue(key, out var confirmed) && confirmed == snapshot)
            {
                continue;
            }
            var choice = decide is not null && projectDirectory is { } project
                ? decide(await services.Files.ReadAsync(project, "device.mcuconfig"))
                : MessageBox.Show(this, "图形化配置尚未保存。\n选择取消返回插件保存；选择确定放弃这次未保存的图形修改。",
                    "未保存的图形配置", MessageBoxButton.OKCancel, MessageBoxImage.None);
            if (choice is MessageBoxResult.Cancel or MessageBoxResult.None or MessageBoxResult.Yes)
            {
                return false;
            }
            discardedConfigurationDrafts[key] = snapshot;
        }
        return true;
    }
}
