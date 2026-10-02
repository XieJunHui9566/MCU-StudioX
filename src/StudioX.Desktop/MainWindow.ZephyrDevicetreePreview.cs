namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Engine;

public partial class MainWindow
{
    /// <summary>用隔离用户数据检查板级 DTS 在工程树和编辑器中的入口，不构建或访问硬件。</summary>
    public async Task RenderZephyrDevicetreePreviewAsync(string directory, string project)
    {
        await OpenProjectAsync(project, CancellationToken.None);
        if (currentProjectManifest is not { Kind: ProjectKind.Zephyr } manifest)
        {
            throw new InvalidOperationException("预览工程不是 Zephyr 工程。");
        }
        var relative = await services.ZephyrProjects.FindBoardDevicetreeAsync(project, manifest);
        if (relative is null || activeDocument?.RelativePath != relative || SourceEditor.IsReadOnly ||
            CodeLanguage.ForFile(relative) != "Devicetree" || SourceEditor.SyntaxHighlighting is null ||
            !SourceEditor.Text.Contains("/dts-v1/;", StringComparison.Ordinal) ||
            ZephyrDeviceTreeButton.Visibility != Visibility.Visible || !ZephyrDeviceTreeButton.IsEnabled ||
            (await FindProjectNodeAsync(relative)) is not { IsSelected: true })
        {
            throw new InvalidOperationException("Zephyr 工程没有自动显示可编辑的板级设备树源文件。");
        }
        await RefreshOutlineAsync();
        if (!outlineSymbols.Any(symbol => symbol is { Kind: 255, Name: "/" } &&
            symbol.Children.Any(child => child is { Kind: 255, Name: "leds" })) ||
            !OutlineStatus.Text.Contains("节点/属性", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("文件结构没有显示当前 DTS 源文件中的节点层级。");
        }
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Render(this, Path.Combine(directory, "zephyr-devicetree.png"));

        var leds = outlineSymbols.SelectMany(symbol => symbol.Children).Single(symbol => symbol is { Kind: 255, Name: "leds" });
        await NavigateOutlineAsync(leds);
        if (SourceEditor.TextArea.Caret.Line != leds.SelectionRange.Start.Line + 1)
        {
            throw new InvalidOperationException("点击设备树节点没有跳转到源文件对应行。");
        }
        await OpenSourceAsync("src/main.c", CancellationToken.None);
        ZephyrDeviceTreeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await pendingOperation;
        if (activeDocument?.RelativePath != relative || !SourceEditor.Text.Contains("/dts-v1/;", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("设备树入口没有返回板级 DTS 源文件。");
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"),
            "PASS: Zephyr board DTS is selected in the project tree, editable with Devicetree highlighting, visible as a node outline with source navigation, and reachable from the device tree button. No build or hardware access.\n");
    }
}
