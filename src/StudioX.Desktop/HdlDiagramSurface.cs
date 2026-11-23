namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using StudioX.Engine.Hdl;

/// <summary>以单个绘图表面显示网表，避免每条连线和引脚都产生 WPF 控件。</summary>
public sealed class HdlDiagramSurface : FrameworkElement
{
    private sealed record Palette(Brush Body, Brush Outline, Brush Text, Brush Wire, Brush Bus);
    private static readonly Palette Dark = new(Brush("#233244"), Brush("#88A6C3"), Brush("#E7EDF6"), Brush("#8AA4BC"), Brush("#85B7D6"));
    private static readonly Palette Light = new(Brush("#F1F5FA"), Brush("#45617F"), Brush("#203246"), Brush("#586D84"), Brush("#24678C"));

    private static readonly DependencyProperty DiagramBackgroundProperty = DependencyProperty.Register(
        "DiagramBackground", typeof(Brush), typeof(HdlDiagramSurface),
        new FrameworkPropertyMetadata(Brush("#1E1F22"), FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty DiagramAccentProperty = DependencyProperty.Register(
        "DiagramAccent", typeof(Brush), typeof(HdlDiagramSurface),
        new FrameworkPropertyMetadata(Brush("#548AF7"), FrameworkPropertyMetadataOptions.AffectsRender));

    private HdlDiagram? diagram;
    private object? selection;
    private (HdlDiagramWire Wire, Geometry Geometry)[] traces = [];
    private (HdlDiagramNode Node, (Geometry Geometry, bool Filled)[] Shapes, HdlSchematicGlyph.Label[] Labels)[] glyphs = [];
    private object? hovered;
    private IReadOnlySet<string>? traceNets;
    private HdlPoint? traceEndpoint;
    public event Action<object?>? SelectionChanged;
    public event Action<HdlDiagramNode>? NodeActivated;
    public HdlDiagram? Diagram => diagram;
    public object? Selection => selection;

    public HdlDiagramSurface()
    {
        SetResourceReference(DiagramBackgroundProperty, "Background");
        SetResourceReference(DiagramAccentProperty, "Accent");
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
            var target = Hit(e.GetPosition(this));
            if (ReferenceEquals(hovered, target))
            {
                return;
            }
            hovered = target;
            ToolTip = target switch
            {
                HdlDiagramNode node => node.Label + "\n" + node.Type + (node.Source is null ? "" : "\n" + node.Source),
                HdlDiagramWire wire => wire.Label + "\n" + string.Join("\n", wire.BitMappings.Take(12)) +
                    (wire.BitMappings.Length > 12 ? "\n… 点击查看全部位连接" : ""),
                _ => null
            };
        };
        MouseLeave += (_, _) =>
        {
            hovered = null;
            ToolTip = null;
        };
    }

    public void SetDiagram(HdlDiagram? value)
    {
        diagram = value;
        Width = value?.Width ?? 600;
        Height = value?.Height ?? 400;
        traces = value?.Wires.Select(wire => (wire, Trace(wire))).ToArray() ?? [];
        glyphs = value?.Nodes.Select(node =>
        {
            var drawing = HdlSchematicGlyph.Create(node);
            return (node, drawing.Paths.Select(path => (FrozenPath(path.Data), path.Filled)).ToArray(), drawing.Labels);
        }).ToArray() ?? [];
        hovered = null;
        ToolTip = null;
        Select(null);
    }

    public void Select(object? value)
    {
        selection = value;
        traceNets = null;
        traceEndpoint = null;
        InvalidateVisual();
        SelectionChanged?.Invoke(value);
    }

    internal void SetTraceHighlight(IReadOnlySet<string>? nets, HdlPoint? endpoint)
    {
        traceNets = nets;
        traceEndpoint = endpoint;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        var background = (Brush)GetValue(DiagramBackgroundProperty);
        var accent = (Brush)GetValue(DiagramAccentProperty);
        var dark = background is not SolidColorBrush solid || (solid.Color.R * 299 + solid.Color.G * 587 + solid.Color.B * 114) < 145000;
        var palette = dark ? Dark : Light;
        context.DrawRectangle(background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (diagram is null)
        {
            Text(context, new("生成电路图后查看逻辑与信号连线", 28, 38, 16, Width: 520), palette.Text);
            return;
        }

        var activeNets = traceNets ?? (selection switch
        {
            HdlDiagramWire wire => new HashSet<string>(StringComparer.Ordinal) { wire.Net },
            HdlDiagramNode node => diagram.Wires.Where(wire => wire.SourceNode == node.Id || wire.TargetNode == node.Id)
                .Select(wire => wire.Net).ToHashSet(StringComparer.Ordinal),
            _ => []
        });
        foreach (var highlighted in new[] { false, true })
        {
            foreach (var (wire, geometry) in traces)
            {
                if (activeNets.Contains(wire.Net) != highlighted)
                {
                    continue;
                }
                var bus = wire.BitMappings.Length > 1;
                var pen = new Pen(highlighted ? accent : bus ? palette.Bus : palette.Wire, highlighted ? 3 : bus ? 2.4 : 1.25);
                context.DrawGeometry(null, pen, geometry);
            }
        }
        foreach (var junction in diagram.Junctions)
        {
            var brush = activeNets.Contains(junction.Net) ? accent : junction.Width > 1 ? palette.Bus : palette.Wire;
            var radius = junction.Width > 1 ? 3.5 : 2.8;
            context.DrawEllipse(brush, new Pen(background, .7), Point(junction.Position), radius, radius);
        }
        foreach (var (node, shapes, labels) in glyphs)
        {
            var selected = Equals(node, selection);
            context.PushTransform(new TranslateTransform(node.X, node.Y));
            var pen = new Pen(selected ? accent : palette.Outline, selected ? 2.1 : 1.45);
            foreach (var (geometry, filled) in shapes)

            {
                context.DrawGeometry(filled ? palette.Body : null, pen, geometry);
            }
            foreach (var label in labels)

            {
                Text(context, label, palette.Text);
            }
            context.Pop();
        }
        foreach (var wire in diagram.Wires)
        {
            if (wire.LabelPosition is not { } point)
            {
                continue;
            }
            var selected = activeNets.Contains(wire.Net);
            var tag = new HdlSchematicGlyph.Label(wire.Label, point.X, point.Y, 11, "middle", 138);
            Text(context, tag, selected ? accent : palette.Text, background);
        }
        if (traceEndpoint is { } endpoint)

        {
            context.DrawEllipse(background, new Pen(accent, 2), Point(endpoint), 4, 4);
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
        HdlDiagramWire? best = null;
        double distance = 5;
        foreach (var wire in diagram.Wires)
        {
            if (wire.LabelPosition is { } label &&
                Math.Abs(point.Y - label.Y) <= 7 &&
                Math.Abs(point.X - label.X) <= Math.Clamp(wire.Label.Length * 3.5 + 4, 15, 69))

            {
                return wire;
            }
            for (int index = 1; index < wire.Points.Length; index++)
            {
                var first = wire.Points[index - 1];
                var next = wire.Points[index];
                double x = Math.Clamp(point.X, Math.Min(first.X, next.X), Math.Max(first.X, next.X));
                double y = Math.Clamp(point.Y, Math.Min(first.Y, next.Y), Math.Max(first.Y, next.Y));
                double offset = Math.Sqrt((x - point.X) * (x - point.X) + (y - point.Y) * (y - point.Y));
                if (offset >= distance)
                {
                    continue;
                }
                best = wire;
                distance = offset;
            }
        }
        return best;
    }

    private void Text(DrawingContext context, HdlSchematicGlyph.Label label, Brush brush, Brush? backdrop = null)
    {
        var formatted = new FormattedText(label.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), label.Size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = label.Width,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };
        var width = formatted.WidthIncludingTrailingWhitespace;
        var x = label.X - (label.Anchor == "middle" ? width / 2 : label.Anchor == "end" ? width : 0);
        var y = label.Y - formatted.Height / 2;
        if (backdrop is not null)

        {
            context.DrawRoundedRectangle(backdrop, null, new Rect(x - 3, y, width + 6, formatted.Height), 2, 2);
        }
        context.DrawText(formatted, new Point(x, y));
    }

    private static Geometry Trace(HdlDiagramWire wire)
    {
        var geometry = new StreamGeometry();
        using (var drawing = geometry.Open())
        {
            foreach (var stroke in wire.Strokes.Length == 0 ? [wire.Points] : wire.Strokes)
            {
                drawing.BeginFigure(Point(stroke[0]), false, false);
                drawing.PolyLineTo(stroke.Skip(1).Select(Point).ToArray(), true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static Geometry FrozenPath(string data)
    {
        var path = Geometry.Parse(data);
        if (path.CanFreeze && !path.IsFrozen)
        {
            path.Freeze();
        }
        return path;
    }

    private static Point Point(HdlPoint point) => new(point.X, point.Y);
    private static Brush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
