namespace StudioX.Desktop;

using System.Windows;
using StudioX.Engine;

public partial class MainWindow
{
    private bool IsZephyrProject => currentProjectManifest?.Kind == ProjectKind.Zephyr;
    private bool IsLegacyZephyrProject => IsZephyrProject && currentProjectManifest?.Zephyr?.FormatVersion != 2;

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
        if ((await FindProjectNodeAsync(relative, token)) is { } node)
        {
            node.IsSelected = true;
            // 只滚动到节点左侧，避免长 DTS 文件名把窄工程树横向推到末尾。
            node.BringIntoView(new Rect(0, 0, 1, Math.Max(1, node.ActualHeight)));
        }
        await OpenSourceAsync(relative, token);
        Status.Text = "板级设备树源文件 · " + relative + "；编译后可在 .build/zephyr/zephyr.dts 查看合成结果。";
    }

}
