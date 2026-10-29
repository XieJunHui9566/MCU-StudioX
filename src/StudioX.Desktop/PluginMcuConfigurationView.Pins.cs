namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StudioX.Extensions.Abstractions;

internal sealed partial class PluginMcuConfigurationView
{
    private double packageZoom = 0.7;
    private Vector packagePan;
    private PluginMcuPackageDiagram? packageDiagram;
    private ContextMenu? pinMenu;
    private Action? dismissPinMenu;
    private bool requestedPinContext;
    private string? requestedMenuPin;
    private PluginMcuConfigurationState? selectionBaseline;

    private FrameworkElement Pins()
    {
        var body = new StackPanel { Margin = new Thickness(12) };
        body.Children.Add(Text(State.PackageName + " · 顶视封装示意", 15, true));
        var legend = new WrapPanel { Margin = new Thickness(0, 8, 0, 10) };
        AddLegend(legend, "未分配", "#9AA2AD");
        AddLegend(legend, "已分配", "#58B67D");
        AddLegend(legend, "电源", "#CB8993");
        AddLegend(legend, "地", "#798492");
        AddLegend(legend, "保留", "#AF9AC4");
        var selectedLegend = new Border { Width = 8, Height = 8, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        selectedLegend.SetResourceReference(BackgroundProperty, "Accent");
        legend.Children.Add(selectedLegend);
        legend.Children.Add(Text("选中外框", 12));
        body.Children.Add(legend);

        var workspace = new Grid();
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var drawing = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
        var zoomBar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        zoomBar.Children.Add(Button("居中适配", () => { packageViewport?.ResetView(); return Task.CompletedTask; }));
        var zoomText = Text(packageZoom.ToString("P0"), 12);
        zoomText.Width = 44;
        DockPanel.SetDock(zoomText, Dock.Right);
        zoomBar.Children.Add(zoomText);
        var zoom = new Slider { Minimum = 0.35, Maximum = 2.5, Value = packageZoom, SmallChange = 0.05, TickFrequency = 0.05, Margin = new Thickness(14, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(zoom, "封装图缩放");
        zoomBar.Children.Add(zoom);
        drawing.Children.Add(zoomBar);
        packageDiagram = new PluginMcuPackageDiagram(State, async pin => await SelectPackagePinAsync(pin), pin => SelectPackagePinAsync(pin, context: true));
        var viewport = new PluginMcuDiagramViewport(packageDiagram, packageZoom, packagePan);
        packageViewport = viewport;
        drawing.Children.Add(viewport);
        drawing.Children.Add(Text("左键分配功能 · 右键命名和锁定 · 拖动平移 · 滚轮缩放", 12));
        viewport.ViewChanged += () =>
        {
            packageZoom = viewport.Zoom;
            packagePan = viewport.Pan;
            zoom.Value = packageZoom;
            zoomText.Text = packageZoom.ToString("P0");
        };
        zoom.ValueChanged += (_, _) =>
        {
            if (Math.Abs(viewport.Zoom - zoom.Value) > 0.0001)
            {
                viewport.SetZoom(zoom.Value);
            }
        };
        Grid.SetColumn(drawing, 1);
        workspace.Children.Add(drawing);
        var settings = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        AddFieldGroups(settings, "peripherals");
        workspace.Children.Add(new ScrollViewer { Content = settings, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(workspace);
        return body;
    }

    private PluginMcuDiagramViewport? packageViewport;

    private async Task SelectPackagePinAsync(PluginMcuPin pin, bool context = false)
    {
        if (HasUnappliedFields())
        {
            await Send(new
            {
                action = "parameters",
                values = fields.ToDictionary(item => item.Key, item => item.Value(), StringComparer.Ordinal)
            });
            if (HasUnappliedFields())
            {
                return;
            }
        }
        ClosePinMenu();
        requestedMenuPin = pin.Id;
        requestedPinContext = context;
        selectionBaseline = State;
        await Send(new
        {
            action = "selectPin",
            pinId = pin.Id
        });
        QueuePinMenu();
    }

    private void QueuePinMenu()
    {
        // 面板事件可能晚于命令响应到达；收到选择快照后，再绑定到新的引脚控件。
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (IsLoaded && IsEnabled && Page == 0 && requestedMenuPin is { } id && State.SelectedPinId == id &&
                !ReferenceEquals(State, selectionBaseline) && packageDiagram is not null && pinMenu?.IsOpen != true)
            {
                OpenPinMenu(State.Pins.Single(item => item.Id == id));
            }
        }));
    }

    private void ClosePinMenu(bool retainRequest = false)
    {
        var dismiss = dismissPinMenu;
        pinMenu = null;
        dismissPinMenu = null;
        if (!retainRequest)
        {
            requestedMenuPin = null;
            selectionBaseline = null;
        }
        dismiss?.Invoke();
    }

    private void OpenPinMenu(PluginMcuPin pin)
    {
        ClosePinMenu(retainRequest: true);
        if (packageDiagram is null)
        {
            return;
        }
        var snapshot = State;
        var anchor = packageDiagram.PinAnchor(pin.Id);
        async Task SubmitAsync(object arguments)
        {
            // 面板刷新或工程切换后，旧菜单不再代表当前插件快照。
            if (IsLoaded && IsEnabled && ReferenceEquals(State, snapshot) && Page == 0)
            {
                requestedMenuPin = null;
                await Send(arguments);
            }
        }
        if (requestedPinContext)
        {
            var context = new PluginMcuPinContextMenu(anchor, pin, SubmitAsync);
            pinMenu = context;
            dismissPinMenu = context.Dismiss;
        }
        else
        {
            var functions = new PluginMcuPinFunctionMenu(anchor, pin, function => SubmitAsync(new { action = "assignPin", pinId = pin.Id, functionId = function }));
            pinMenu = functions;
            dismissPinMenu = functions.Dismiss;
        }
        anchor.ContextMenu = pinMenu;
        var menu = pinMenu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(pinMenu, menu))
            {
                requestedMenuPin = null;
            }
        };
        pinMenu.IsOpen = true;
    }

    private static void AddLegend(Panel panel, string label, string color)
    {
        panel.Children.Add(new Border { Width = 8, Height = 8, Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), CornerRadius = new CornerRadius(1), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        var text = Text(label, 12);
        text.Margin = new Thickness(0, 0, 12, 0);
        panel.Children.Add(text);
    }
}
