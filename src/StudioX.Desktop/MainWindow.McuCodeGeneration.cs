namespace StudioX.Desktop;

using System.Windows;
using StudioX.Application.Plugins;

public partial class MainWindow
{
    private async Task SaveMcuConfigurationAsync(PluginWorkspaceSession workspace, string pluginId, string commandId, CancellationToken token)
    {
        var committed = false;
        try
        {
            await services.McuCodeGeneration.SaveConfigurationAsync(workspace, pluginId, commandId,
                () => Dispatcher.InvokeAsync(() => editorDocuments.Any(document => document.IsDirty)).Task, token);
            committed = true;
            if (!ReferenceEquals(FindPluginSession(pluginId), workspace))
            {
                return;
            }
            await ResynchronizeProjectFilesAsync();
            await services.McuCodeGeneration.RefreshSavedConfigurationAsync(workspace, pluginId, commandId, CancellationToken.None);
            Status.Text = "图形配置已保存。";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            MessageBox.Show(this, (committed ? "配置已保存，但界面刷新失败；请保留草稿后重新打开工程。\n" : "") + error.Message,
                "保存图形配置", MessageBoxButton.OK, MessageBoxImage.Warning);
            throw;
        }
    }

    private async Task GenerateMcuCodeAsync(PluginWorkspaceSession workspace, string pluginId, string commandId, CancellationToken token)
    {
        var committed = false;
        try
        {
            var count = await services.McuCodeGeneration.GenerateAsync(workspace, pluginId, commandId,
                () => Dispatcher.InvokeAsync(() => editorDocuments.Any(document => document.IsDirty)).Task, token);
            committed = true;
            if (!ReferenceEquals(FindPluginSession(pluginId), workspace))
            {
                return;
            }
            await ResynchronizeProjectFilesAsync();
            await services.McuCodeGeneration.RefreshAsync(workspace, pluginId, commandId, CancellationToken.None);
            Status.Text = $"已生成 {count} 个 C 源码和头文件，main.c 与构建列表已更新。";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            MessageBox.Show(this, (committed ? "代码已写入工程，但界面刷新失败；请保留草稿后重新打开工程。\n" : "") + error.Message,
                committed ? "代码已生成" : "生成代码未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
            throw;
        }
    }
}
