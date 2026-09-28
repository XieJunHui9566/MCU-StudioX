namespace StudioX.Desktop;

using System.Windows;
using Microsoft.Win32;
using StudioX.Engine;

public partial class MainWindow
{
    private bool IsZephyrProject => currentProjectManifest?.Kind == ProjectKind.Zephyr;

    private async void ZephyrDeviceTree_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (projectDirectory is not { } directory || currentProjectManifest is not { Kind: ProjectKind.Zephyr } project)
        {
            return;
        }
        var relative = await services.ZephyrProjects.FindBoardDevicetreeAsync(directory, project, token);
        if (relative is null)
        {
            ZephyrDeviceTreeButton.IsEnabled = false;
            Status.Text = "当前 Zephyr 工程没有板级 DTS 源文件；可检查 boards 目录或模板内容。";
            return;
        }
        await ShowZephyrBoardDevicetreeAsync(relative, token);
    });

    private async Task ShowZephyrBoardDevicetreeAsync(string relative, CancellationToken token)
    {
        if (FindProjectNode(relative) is { } node)
        {
            node.IsSelected = true;
            // 只滚动到节点左侧，避免长 DTS 文件名把窄工程树横向推到末尾。
            node.BringIntoView(new Rect(0, 0, 1, Math.Max(1, node.ActualHeight)));
        }
        await OpenSourceAsync(relative, token);
        Status.Text = "板级设备树源文件 · " + relative + "；合成后的 zephyr.dts 需通过 west 构建。";
    }

    private async void ZephyrExperimental_Click(object sender, RoutedEventArgs e)
    {
        var selection = new ZephyrProjectWindow(services.ZephyrPacks) { Owner = this };
        if (selection.ShowDialog() != true)
        {
            return;
        }
        var destinationPicker = new OpenFolderDialog { Title = "选择 Zephyr 实验工程的父目录（路径请勿含空格）" };
        if (destinationPicker.ShowDialog(this) != true)
        {
            return;
        }
        await RunAsync(async token =>
        {
            var destination = Path.Combine(destinationPicker.FolderName, selection.ProjectName);
            var project = await services.ZephyrProjects.CreateAsync(selection.SelectedPack, selection.SelectedBoard.Id,
                selection.SelectedTemplate.Id, selection.ProjectName, destination, token);
            Log($"已创建 Zephyr 实验工程：{project.Name} · {project.Zephyr?.BoardTarget}。Git 仓库已初始化。");
            await OpenProjectAsync(destination, token);
            Status.Text = "Zephyr 实验模式 · 工程已创建；板级构建和调试尚待验证。";
        });
    }
}
