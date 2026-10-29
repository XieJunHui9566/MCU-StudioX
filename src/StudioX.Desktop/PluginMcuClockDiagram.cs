namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using StudioX.Extensions.Abstractions;

/// <summary>有界的时钟符号图；拓扑、符号、选中路径和数值均由隔离插件提供。</summary>
internal sealed class PluginMcuClockDiagram : Canvas
{
    internal PluginMcuClockDiagram(PluginMcuClock[] clocks, PluginMcuField[]? fields = null, Func<PluginMcuField, FrameworkElement>? createControl = null)
    {
        Width = Math.Max(600, clocks.Select(node => node.X + Size(node).Width + 24).DefaultIfEmpty(600).Max());
        Height = Math.Max(180, clocks.Select(node => node.Y + Size(node).Height + 90).DefaultIfEmpty(180).Max());
        Background = Brushes.Transparent;
        AutomationProperties.SetName(this, "MCU 时钟树");
        var edges = clocks.SelectMany(node => node.InputIds.Select(id => (Source: clocks.Single(source => source.Id == id), Target: node, Id: id)));
        foreach (var edge in edges.OrderBy(edge => Active(edge.Source, edge.Target, edge.Id)))
        {
            Connect(edge.Source, edge.Target, Active(edge.Source, edge.Target, edge.Id));
        }
        foreach (var node in clocks)
        {
            DrawNode(node);
        }
        foreach (var field in fields ?? [])
        {
            var node = clocks.Single(clock => clock.Id == field.ClockNodeId);
            var control = createControl!(field);
            control.Width = node.Kind == "multiplexer" ? 166 : 144;
            control.Height = 32;
            if (control is Control input)
            {
                input.FontSize = 13;
            }
            control.ToolTip = field.Label;
            var x = node.X + (Size(node).Width - control.Width) / 2;
            var y = node.Y + Size(node).Height + (node.Kind == "source" ? 35 : 60);
            Place(control, x, y);
            if (node.Kind == "source")
            {
                Label("MHz", x + control.Width + 5, y + 10, 36, 12, "Muted");
            }
        }
    }

    private static bool Active(PluginMcuClock source, PluginMcuClock target, string input)
        => source.Enabled && target.Enabled && (target.ActiveInputIds is null || target.ActiveInputIds.Contains(input, StringComparer.Ordinal));

    private static Size Size(PluginMcuClock node) => node.Kind switch
    {
        "multiplexer" => new(68, 128),
        "divider" => new(112, 64),
        "junction" => new(90, 42),
        _ => new(144, 64)
    };

    private static double InputY(PluginMcuClock node, string id)
        => node.Y + Size(node).Height * (Array.IndexOf(node.InputIds, id) + 1) / (node.InputIds.Length + 1);

    private void Connect(PluginMcuClock source, PluginMcuClock target, bool active)
    {
        var start = new Point(source.X + Size(source).Width, source.Y + Size(source).Height / 2);
        var finish = new Point(target.X, InputY(target, source.Id));
        var turn = source.Kind == "junction" ? start.X + 24 : (start.X + finish.X) / 2;
        Wire([start, new(turn, start.Y), new(turn, finish.Y), finish], active);
        var arrow = new Polygon { Points = new PointCollection([new(finish.X - 6, finish.Y - 3), finish, new(finish.X - 6, finish.Y + 3)]), IsHitTestVisible = false };
        arrow.SetResourceReference(Shape.FillProperty, active ? "RunColor" : "Muted");
        Children.Add(arrow);
        if (source.Kind == "junction")
        {
            var dot = new Ellipse { Width = 5, Height = 5, IsHitTestVisible = false };
            dot.SetResourceReference(Shape.FillProperty, active ? "RunColor" : "Muted");
            Place(dot, turn - 2.5, finish.Y - 2.5);
        }
    }

    private void Wire(IEnumerable<Point> points, bool active)
    {
        var line = new Polyline
        {
            Points = new PointCollection(points),
            StrokeThickness = active ? 1.8 : 1,
            IsHitTestVisible = false
        };
        line.SetResourceReference(Shape.StrokeProperty, active ? "RunColor" : "Muted");
        Children.Add(line);
    }

    private void DrawNode(PluginMcuClock node)
    {
        var size = Size(node);
        var key = node.Enabled ? "RunColor" : "Muted";
        Label(node.Name, node.X, node.Y - (node.Kind == "divider" ? 38 : 27), size.Width, 12, "Text", true);
        if (node.Kind == "junction")
        {
            Wire([new(node.X, node.Y + size.Height / 2), new(node.X + size.Width, node.Y + size.Height / 2)], node.Enabled);
            Label(node.Value, node.X - 8, node.Y + 35, size.Width + 16, 13, key, true);
            return;
        }
        Shape outline = node.Kind == "multiplexer"
            ? new Polygon { Points = new PointCollection([new(0, 0), new(size.Width, 24), new(size.Width, size.Height - 24), new(0, size.Height)]) }
            : new Rectangle { Width = size.Width, Height = size.Height, RadiusX = 2, RadiusY = 2 };
        outline.StrokeThickness = node.Enabled ? 1.7 : 1;
        outline.SetResourceReference(Shape.FillProperty, "ChromeSurface");
        outline.SetResourceReference(Shape.StrokeProperty, key);
        Place(outline, node.X, node.Y);
        if (node.Kind == "multiplexer")
        {
            for (var index = 0; index < node.InputIds.Length; index++)
            {
                var id = node.InputIds[index];
                var y = InputY(node, id);
                Label(index.ToString(System.Globalization.CultureInfo.InvariantCulture), node.X + 5, y - 15, 18, 10, "Muted");
                if (node.Enabled && node.ActiveInputIds?.Contains(id, StringComparer.Ordinal) == true)
                {
                    Wire([new(node.X, y), new(node.X + size.Width - 10, node.Y + size.Height / 2), new(node.X + size.Width, node.Y + size.Height / 2)], true);
                }
            }
            Label(node.Value, node.X - 12, node.Y + size.Height + 8, size.Width + 24, 11, key);
        }
        else if (node.Kind == "source")
        {
            Shape wave = node.Symbol == "crystal"
                ? new System.Windows.Shapes.Path { Data = Geometry.Parse("M 0 20 H 5 M 5 8 V 32 M 9 11 H 19 V 29 H 9 Z M 23 8 V 32 M 23 20 H 28"), StrokeThickness = 1.5 }
                : new Polyline { Points = new PointCollection([new(0, 24), new(0, 10), new(12, 10), new(12, 24), new(24, 24), new(24, 10)]), StrokeThickness = 1.5 };
            wave.SetResourceReference(Shape.StrokeProperty, key);
            Place(wave, node.X + 12, node.Y + 14);
            Label(node.Value, node.X + 43, node.Y + 21, size.Width - 47, 13, key, true);
        }
        else if (node.Kind == "divider")
        {
            Label(node.Symbol, node.X, node.Y + 15, size.Width, 23, key, true);
            Label(node.Value, node.X - 4, node.Y + size.Height + 7, size.Width + 8, 13, key, true);
        }
        else
        {
            Label(node.Value, node.X + 6, node.Y + 17, size.Width - 12, 13, key, true);
        }
        if (node.Detail.Length > 0)
        {
            var detailY = node.Kind == "divider" ? node.Y - 18 : node.Kind == "sink" ? node.Y + 43 : node.Y + size.Height + (node.Kind == "multiplexer" ? 29 : 8);
            Label(node.Detail, node.X - 14, detailY, size.Width + 28, 10, "Muted");
        }
    }

    private void Label(string value, double x, double y, double width, double size, string resource, bool bold = false)
    {
        var text = new TextBlock
        {
            Text = value,
            Width = width,
            FontSize = size,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            IsHitTestVisible = false
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, resource);
        Place(text, x, y);
    }

    private void Place(FrameworkElement element, double x, double y)
    {
        SetLeft(element, x);
        SetTop(element, y);
        Children.Add(element);
    }
}
