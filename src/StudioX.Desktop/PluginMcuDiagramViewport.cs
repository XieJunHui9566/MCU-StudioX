namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

/// <summary>图形视口：保留引脚单击与表单操作，越过拖动阈值后才捕获鼠标。</summary>
internal sealed class PluginMcuDiagramViewport : Canvas
{
    private readonly FrameworkElement diagram;
    private readonly double minimumZoom;
    private Point? pressed;
    private Vector startPan;
    private bool dragging;
    internal double Zoom
    {
        get; private set;
    }
    internal Vector Pan
    {
        get; private set;
    }
    internal event Action? ViewChanged;

    internal PluginMcuDiagramViewport(FrameworkElement diagram, double zoom, Vector pan, string name = "封装交互画布", double minimumZoom = 0.35)
    {
        this.diagram = diagram;
        this.minimumZoom = minimumZoom;
        Zoom = zoom;
        Pan = pan;
        Height = 500;
        ClipToBounds = true;
        Background = Brushes.Transparent;
        Children.Add(diagram);
        AutomationProperties.SetName(this, name);
        SizeChanged += (_, _) => TransformDiagram();
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            BeginPan(e.GetPosition(this), e.OriginalSource as DependencyObject);
        };
        AddHandler(MouseMoveEvent, new MouseEventHandler((_, e) =>
        {
            if (ContinuePan(e.GetPosition(this), e.LeftButton))
            {
                e.Handled = true;
            }
        }), true);
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            e.Handled = dragging;
            EndDrag();
        };
        LostMouseCapture += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, this) && !IsMouseCaptured)
            {
                EndDrag();
            }
        };
        Unloaded += (_, _) => EndDrag();
        PreviewMouseWheel += (_, e) =>
        {
            e.Handled = ZoomWheel(e.OriginalSource as DependencyObject, e.Delta, e.GetPosition(this));
        };
    }

    internal bool ZoomWheel(DependencyObject? source, int delta, Point anchor)
    {
        if (IsField(source))
        {
            return false;
        }
        SetZoom(Zoom * Math.Pow(1.12, delta / 120.0), anchor);
        return true;
    }

    internal void BeginPan(Point position, DependencyObject? source)
    {
        pressed = IsField(source) ? null : position;
        startPan = Pan;
    }

    internal bool ContinuePan(Point position, MouseButtonState button)
    {
        if (pressed is not { } origin || button != MouseButtonState.Pressed)
        {
            return false;
        }
        var delta = position - origin;
        if (!dragging && (Math.Abs(delta.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(delta.Y) > SystemParameters.MinimumVerticalDragDistance))
        {
            dragging = CaptureMouse();
            Cursor = dragging ? Cursors.SizeAll : null;
        }
        if (dragging)
        {
            Pan = startPan + delta;
            TransformDiagram();
            ViewChanged?.Invoke();
        }
        return dragging;
    }

    private bool IsField(DependencyObject? source)
    {
        // 时钟节点中的下拉、文本选择和滑块需要原有鼠标行为，不能被画布抢走。
        for (var element = source; element is not null && !ReferenceEquals(element, this);)
        {
            if (element is TextBoxBase or Selector or RangeBase)
            {
                return true;
            }
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }

    internal void SetZoom(double value, Point? anchor = null)
    {
        var next = Math.Clamp(value, minimumZoom, 2.5);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var point = anchor ?? center;
        // 保持指针下的图形坐标不动，避免滚轮缩放时跳向左上角。
        Pan = (point - center) - ((point - center) - Pan) * (next / Zoom);
        Zoom = next;
        TransformDiagram();
        ViewChanged?.Invoke();
    }

    internal void ResetView()
    {
        Zoom = Math.Clamp(Math.Min((ActualWidth - 32) / diagram.Width, (ActualHeight - 32) / diagram.Height), minimumZoom, 1);
        Pan = new Vector();
        TransformDiagram();
        ViewChanged?.Invoke();
    }

    private void TransformDiagram() => diagram.RenderTransform = new MatrixTransform(Zoom, 0, 0, Zoom,
        (ActualWidth - diagram.Width * Zoom) / 2 + Pan.X, (ActualHeight - diagram.Height * Zoom) / 2 + Pan.Y);

    private void EndDrag()
    {
        dragging = false;
        pressed = null;
        Cursor = null;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }
}
