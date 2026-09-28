namespace StudioX.Desktop;

using StudioX.Engine;
using System.Windows;

public partial class MainWindow
{
    private bool IsMicroPythonProject => currentProjectManifest?.Kind == ProjectKind.MicroPython;
    private Task microPythonRunTask = Task.CompletedTask;

    private async void MicroPythonRun_Click(object sender, RoutedEventArgs e)
    {
        if (!IsMicroPythonProject || projectActionsBusy || MicroPythonPanel.IsBusy) { return; }
        ShowDocument(MicroPythonTab);
        // 持续运行由页面持有，工程切换和关闭窗口仍可取消它。
        microPythonRunTask = MicroPythonPanel.StartScriptAsync();
        await microPythonRunTask;
        Status.Text = MicroPythonPanel.OperationStatus;
    }

    private async Task DownloadMicroPythonAsync(string root, CancellationToken token)
    {
        ShowDocument(MicroPythonTab);
        if (MicroPythonPanel.IsBusy)
        {
            Status.Text = "请等待当前 MicroPython 操作完成，或点击停止。";
            return;
        }
        await SaveAllSourcesAsync(root, token);
        await MicroPythonPanel.DownloadAsync(token);
        Status.Text = MicroPythonPanel.OperationStatus;
    }
}
