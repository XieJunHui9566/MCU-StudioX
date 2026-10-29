namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

internal sealed partial class PluginMcuConfigurationView
{
    private double clockZoom = 0.65;
    private Vector clockPan;
    private bool clockViewInitialized;

    private FrameworkElement Parameters()
    {
        var body = new StackPanel { Margin = new Thickness(14) };
        var actions = new DockPanel();
        var apply = Button("应用参数并检查", () => Send(new { action = "parameters", values = fields.ToDictionary(item => item.Key, item => item.Value(), StringComparer.Ordinal) }));
        DockPanel.SetDock(apply, Dock.Right);
        actions.Children.Add(apply);
        actions.Children.Add(Text("选择后自动更新时钟树；绿色表示当前路径。", 12));
        body.Children.Add(actions);
        var diagram = new PluginMcuClockDiagram(State.Clocks, State.Fields.Where(field => field.Page == "clock" && field.ClockNodeId.Length > 0).ToArray(), CreateFieldControl);
        var viewport = new PluginMcuDiagramViewport(diagram, clockZoom, clockPan, "时钟树交互画布", minimumZoom: 0.15) { Height = 520 };
        var zoomBar = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        zoomBar.Children.Add(Button("居中适配", () => { viewport.ResetView(); return Task.CompletedTask; }));
        var zoomText = Text(clockZoom.ToString("P0"), 12);
        zoomText.Width = 44;
        DockPanel.SetDock(zoomText, Dock.Right);
        zoomBar.Children.Add(zoomText);
        var zoom = new Slider { Minimum = 0.15, Maximum = 2.5, Value = clockZoom, SmallChange = 0.05, TickFrequency = 0.05, Margin = new Thickness(14, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(zoom, "时钟树缩放");
        zoomBar.Children.Add(zoom);
        body.Children.Add(zoomBar);
        viewport.ViewChanged += () =>
        {
            clockZoom = viewport.Zoom;
            clockPan = viewport.Pan;
            zoom.Value = clockZoom;
            zoomText.Text = clockZoom.ToString("P0");
        };
        zoom.ValueChanged += (_, _) =>
        {
            if (Math.Abs(viewport.Zoom - zoom.Value) > 0.0001)
            {
                viewport.SetZoom(zoom.Value);
            }
        };
        viewport.Loaded += (_, _) =>
        {
            // 只在首次打开时适配，参数刷新或切换页面不打断用户正在查看的区域。
            if (!clockViewInitialized)
            {
                clockViewInitialized = true;
                viewport.ResetView();
            }
        };
        var drawing = new Border
        {
            Padding = new Thickness(12),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 8, 0, 4),
            Child = viewport
        };
        drawing.SetResourceReference(BackgroundProperty, "Surface");
        drawing.SetResourceReference(Border.BorderBrushProperty, "Border");
        body.Children.Add(drawing);
        body.Children.Add(Text("左键拖动平移 · 滚轮缩放 · 居中适配显示完整时钟树", 12));
        if (State.Fields.Any(field => field.Page == "clock" && field.ClockNodeId.Length == 0))
        {
            AddFieldGroups(body, "clock");
        }
        return body;
    }
}
