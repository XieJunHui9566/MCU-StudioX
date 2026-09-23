namespace StudioX.Desktop;

using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

public partial class MainWindow
{
    /// <summary>隔离窗口中检查最大化边界和日志末行；不打开工程、不构建或访问设备。</summary>
    public async Task RenderWindowLayoutPreviewAsync(string directory)
    {
        var report = new StringBuilder();
        var failed = false;
        // 预览宿主起初在屏幕外；先移入主屏，按真实用户窗口的位置测试最大化。
        Left = SystemParameters.WorkArea.Left + 16;
        Top = SystemParameters.WorkArea.Top + 16;
        ShowBottom(0);
        BuildLog.Text = string.Join('\n', Enumerable.Range(1, 180).Select(i => $"[{i:000}] 窗口布局检查 · 构建输出")) + "\nEND — 最后一行应完整可见";
        DeviceLog.Text = BuildLog.Text;
        foreach (var state in new[] { WindowState.Normal, WindowState.Maximized, WindowState.Normal })
        {
            WindowState = state;
            await Layout();
            foreach (var height in new[] { 150d, 400d })
            {
                BottomRow.Height = new GridLength(height);
                await Layout();
                foreach (var tab in new[] { 0, 1 })
                {
                    BottomTabs.SelectedIndex = tab;
                    var log = tab == 0 ? BuildLog : DeviceLog;
                    await Layout(); log.ScrollToEnd(); await Layout();
                    var line = log.GetRectFromCharacterIndex(log.Text.Length, true);
                    var scroll = (ScrollViewer)log.Template.FindName("PART_ContentHost", log);
                    var statusBottom = Status.PointToScreen(new Point(0, Status.ActualHeight)).Y;
                    var logBottom = log.PointToScreen(new Point(0, log.ActualHeight)).Y;
                    var lineBottom = log.PointToScreen(line.BottomLeft).Y;
                    if (!WindowMonitor.TryGetBounds(new WindowInteropHelper(this).Handle, out _, out var work))
                        throw new InvalidOperationException("无法读取当前屏幕工作区。");
                    var atEnd = Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1;
                    var lineVisible = !line.IsEmpty && line.Top >= 0 && line.Bottom <= log.ActualHeight;
                    var fits = state != WindowState.Maximized || (statusBottom <= work.Bottom && logBottom <= work.Bottom && lineBottom <= work.Bottom);
                    report.AppendLine($"{state}, panel={height}, tab={tab}: workBottom={work.Bottom}, statusBottom={statusBottom}, logBottom={logBottom}, lastLineBottom={lineBottom}, atEnd={atEnd}, lineVisible={lineVisible}, fitsWorkArea={fits}");
                    failed |= !atEnd || !lineVisible || !fits;
                }
            }
            Render(this, Path.Combine(directory, $"layout-{state}.png"));
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "layout.txt"), report.ToString());
        if (failed) throw new InvalidOperationException("窗口边界或日志末行被裁切，详见 layout.txt。");
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: normal/maximized/restored window; both log tabs at 150/400 px panel heights; last line and status inside the visible work area. No project or hardware actions.\n");
        async Task Layout() { UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render); }
    }
}
