namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StudioX.Engine.Hdl;

/// <summary>以单个绘图表面显示网表，避免每条连线和引脚都产生 WPF 控件。</summary>
public sealed class HdlDiagramSurface : FrameworkElement
{
    private static readonly Brush BackgroundBrush = Brush("#141B26");
    private static readonly Brush TextBrush = Brush("#E2EAF5");
    private static readonly Brush AccentBrush = Brush("#78DABC");
    private HdlDiagram? diagram;
    private object? selection;
    public event Action<object?>? SelectionChanged;
    public event Action<HdlDiagramNode>? NodeActivated;

    public HdlDiagram? Diagram => diagram;
    public object? Selection => selection;

    public HdlDiagramSurface()
    {
        Focusable = true;
        MouseLeftButtonDown += (_, e) =>
        {
            Focus();
            Select(Hit(e.GetPosition(this)));
            if (e.ClickCount == 2 && selection is HdlDiagramNode node)
            {
                NodeActivated?.Invoke(node);
            }
            e.Handled = true;
        };
        MouseMove += (_, e) =>
        {
            ToolTip = Hit(e.GetPosition(this)) switch
            {
                HdlDiagramNode node => node.Label + "\n" + node.Type + "\n" + node.Source,
                HdlDiagramWire wire => wire.Label + "\n" + string.Join("\n", wire.BitMappings),
                _ => null,
            };
        };
    }

    public void SetDiagram(HdlDiagram? value)
    {
        diagram = value;
        Width = value?.Width ?? 600;
        Height = value?.Height ?? 400;
        Select(null);
    }

    public void Select(object? value)
    {
        selection = value;
        InvalidateVisual();
        SelectionChanged?.Invoke(value);
    }

    protected override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (diagram is null)
        {
            Text(context, "生成电路图后，在这里查看逻辑与信号连线", 28, 30, 16, TextBrush);
            return;
        }
        foreach (var wire in diagram.Wires)
        {
            var highlighted = Equals(selection, wire) || selection is HdlDiagramNode node &&
                (wire.SourceNode == node.Id || wire.TargetNode == node.Id);
            var pen = new Pen(highlighted ? AccentBrush : Brush("#617B9A"), highlighted ? 3 : wire.BitMappings.Length > 1 ? 2 : 1);
            var geometry = new StreamGeometry();
            using (var drawing = geometry.Open())
            {
                drawing.BeginFigure(Point(wire.Points[0]), false, false);
                drawing.PolyLineTo(wire.Points.Skip(1).Select(Point).ToArray(), true, false);
            }
            geometry.Freeze();
            context.DrawGeometry(null, pen, geometry);
        }
        foreach (var node in diagram.Nodes)
        {
            context.DrawRoundedRectangle(Brush("#243349"), new Pen(Equals(selection, node) ? AccentBrush : Brush("#7295BD"), 1.5),
                new Rect(node.X, node.Y, node.Width, node.Height), 5, 5);
            Text(context, node.Label, node.X + 12, node.Y + 8, 14, TextBrush, node.Width - 24);
            if (node.Type is not ("input" or "output" or "inout" or "constant"))
            {
                Text(context, node.Symbol, node.X + 12, node.Y + 29, 12, AccentBrush, node.Width - 24);
            }
            foreach (var pin in node.Inputs.Concat(node.Outputs))
            {
                var output = node.Outputs.Contains(pin);
                var label = pin.Name + (pin.Bits.Length > 1 ? " [" + pin.Bits.Length + "]" : "");
                Text(context, label, output ? node.X + node.Width / 2 : node.X + 9,
                    pin.Position.Y - 8, 11, TextBrush, node.Width / 2 - 18, output);
                context.DrawEllipse(AccentBrush, null, Point(pin.Position), 3, 3);
            }
        }
    }

    private object? Hit(Point point)
    {
        if (diagram is null)
        {
            return null;
        }
        var node = diagram.Nodes.FirstOrDefault(item => new Rect(item.X, item.Y, item.Width, item.Height).Contains(point));
        if (node is not null)
        {
            return node;
        }
        return diagram.Wires.LastOrDefault(wire => wire.Points.Zip(wire.Points.Skip(1)).Any(pair =>
            new Rect(new Point(Math.Min(pair.First.X, pair.Second.X) - 5, Math.Min(pair.First.Y, pair.Second.Y) - 5),
                new Point(Math.Max(pair.First.X, pair.Second.X) + 5, Math.Max(pair.First.Y, pair.Second.Y) + 5)).Contains(point)));
    }

    private void Text(DrawingContext context, string text, double x, double y, double size, Brush brush,
        double width = 1000, bool right = false)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = width,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
        };
        context.DrawText(formatted, new Point(x, y));
    }

    private static Point Point(HdlPoint point) => new(point.X, point.Y);
    private static Brush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
