namespace StudioX.Desktop;

using System.Windows;
using Microsoft.Win32;

public partial class MainWindow
{
    private async void ImportCubeMx_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 CubeMX CMake 工程根目录（包含 .ioc 和 CMakeLists.txt）" };
        if (dialog.ShowDialog(this) == true)
        {
            await RunAsync(token => ImportCubeMxAsync(dialog.FolderName, token));
        }
    }
    public Task ShowCubeMxImportAsync(string? directory = null) => RunAsync(async token =>
    {
        if (directory is null)
        {
            var picker = new OpenFolderDialog { Title = "选择 CubeMX CMake 工程根目录" };
            if (picker.ShowDialog(this) != true)
            {
                return;
            }
            directory = picker.FolderName;
        }
        await ImportCubeMxAsync(directory, token);
    });
    private async Task ImportCubeMxAsync(string directory, CancellationToken token)
    {
        if (File.Exists(Path.Combine(directory, ".studiox/project.json")))
        {
            await OpenProjectAsync(directory, token);
            return;
        }
        Status.Text = "正在识别 CubeMX 工程与 CMake 预设…";
        var progress = new Progress<string>(text => Status.Text = text);
        var inspection = await services.CubeMx.InspectAsync(directory, token, progress);
        var dialog = new CubeMxImportWindow(inspection) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            Status.Text = "已取消导入";
            return;
        }
        if (!await CloseProjectAsync(token))
        {
            return;
        }
        await services.CubeMx.ImportAsync(directory, dialog.ConfigurePreset, dialog.BuildType, token, progress);
        await OpenProjectAsync(directory, token);
    }
    private async Task<bool> ConfigureCubeMxAsync(string directory, CancellationToken token)
    {
        Status.Text = "正在配置 CubeMX 工程…";
        var report = await ConfigureWithOutputAsync(directory, token);
        return report.Success;
    }
}
