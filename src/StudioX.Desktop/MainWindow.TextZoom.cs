namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StudioX.Application;

public partial class MainWindow
{
    private Task pendingZoomSave = Task.CompletedTask;
    private int zoomWheelDelta;
    private FrameworkElement? zoomWheelTarget;

    internal bool HandleTextMouseWheel(DependencyObject? hit, int delta, bool control)
    {
        if (closing || !IsEnabled) return false;
        return ProjectTerminal?.HandleMouseWheel(hit, delta, control) == true || SerialPlotView?.HandlePlotMouseWheel(hit, delta, control) == true || SerialView?.HandleReceiveMouseWheel(hit, delta, control) == true ||
            ApplyWheelZoom(FindZoomTarget(hit), delta, control);
    }
    private FrameworkElement? FindZoomTarget(DependencyObject? element)
    {
        for (; element is not null; element = element is Visual ? VisualTreeHelper.GetParent(element) :
            element is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(element))
            if (element == SourceEditor || element == BuildLog || element == DeviceLog) return (FrameworkElement)element;
        return null;
    }
    private bool ApplyWheelZoom(FrameworkElement? target, int delta, bool control)
    {
        if (!control || target is null) { zoomWheelDelta = 0; zoomWheelTarget = null; return false; }
        if (zoomWheelTarget != target) { zoomWheelDelta = 0; zoomWheelTarget = target; }
        zoomWheelDelta += delta;
        var steps = zoomWheelDelta / Mouse.MouseWheelDeltaForOneLine;
        zoomWheelDelta %= Mouse.MouseWheelDeltaForOneLine;
        if (steps != 0)
        {
            if (target == SourceEditor) ZoomEditor(steps);
            else if (target is TextBox log)
            {
                log.FontSize = Math.Clamp(log.FontSize + steps, 10, 28);
                Status.Text = $"输出字号：{log.FontSize:0} px";
            }
        }
        return true;
    }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomEditor(1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomEditor(-1);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => ZoomEditor((int)(new EditorSettings().FontSize - editorSettings.FontSize));
    private void ZoomEditor(int steps)
    {
        var size = Math.Clamp(editorSettings.FontSize + steps, 10, 28);
        if (size == editorSettings.FontSize) return;
        CloseCodeAssistance();
        var settings = editorSettings with { FontSize = size };
        ApplyEditorSettings(settings);
        Status.Text = $"编辑器字号：{size:0} px";
        // 串行落盘；快速滚动时旧字号不能覆盖新值。关闭窗口或打开外观设置前等待保存完成。
        pendingZoomSave = SaveZoomAsync(pendingZoomSave, settings);
    }
    private async Task SaveZoomAsync(Task previous, EditorSettings settings)
    {
        await previous;
        try { await services.EditorSettings.SaveAsync(settings); }
        catch (Exception ex) { Log("字号保存失败：" + ex); Status.Text = "字号已调整，保存偏好失败。"; }
    }
}
