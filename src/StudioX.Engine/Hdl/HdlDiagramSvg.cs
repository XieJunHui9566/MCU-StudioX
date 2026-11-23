namespace StudioX.Engine.Hdl;

using System.Globalization;
using System.Xml.Linq;

/// <summary>导出自包含 SVG；源码标识符只作为转义后的文本，不执行脚本或加载外部资源。</summary>
public static class HdlDiagramSvg
{
    public static string Write(HdlDiagram diagram)
    {
        XNamespace ns = "http://www.w3.org/2000/svg";
        const string canvas = "#1e1f22", outline = "#88a6c3", body = "#233244", ink = "#e7edf6", wireColor = "#8aa4bc", busColor = "#85b7d6";
        var root = new XElement(ns + "svg", new XAttribute("viewBox", $"0 0 {N(diagram.Width)} {N(diagram.Height)}"),
            new XAttribute("width", N(diagram.Width)), new XAttribute("height", N(diagram.Height)),
            new XElement(ns + "title", diagram.ModuleName),
            new XElement(ns + "rect", new XAttribute("width", "100%"), new XAttribute("height", "100%"), new XAttribute("fill", canvas)));
        foreach (var wire in diagram.Wires)
        {
            var bus = wire.BitMappings.Length > 1;
            root.Add(new XElement(ns + "g", new XAttribute("fill", "none"),
                new XAttribute("stroke", bus ? busColor : wireColor), new XAttribute("stroke-width", bus ? "2.4" : "1.25"),
                new XElement(ns + "title", wire.Label + "\n" + string.Join("\n", wire.BitMappings)),
                (wire.Strokes.Length == 0 ? [wire.Points] : wire.Strokes).Select(stroke =>
                    new XElement(ns + "polyline", new XAttribute("points", string.Join(" ", stroke.Select(point => N(point.X) + "," + N(point.Y))))))));
        }
        foreach (var junction in diagram.Junctions)
        {
            var bus = junction.Width > 1;
            root.Add(new XElement(ns + "circle", new XAttribute("cx", N(junction.Position.X)), new XAttribute("cy", N(junction.Position.Y)),
                new XAttribute("r", bus ? "3.5" : "2.8"), new XAttribute("fill", bus ? busColor : wireColor),
                new XAttribute("stroke", canvas), new XAttribute("stroke-width", ".7")));
        }
        foreach (var node in diagram.Nodes)
        {
            var draw = HdlSchematicGlyph.Create(node);
            var group = new XElement(ns + "g", new XAttribute("transform", $"translate({N(node.X)} {N(node.Y)})"),
                new XElement(ns + "title", node.Label + "\n" + node.Type));
            foreach (var shape in draw.Paths)

            {
                group.Add(new XElement(ns + "path", new XAttribute("d", shape.Data), new XAttribute("fill", shape.Filled ? body : "none"),
                                    new XAttribute("stroke", outline), new XAttribute("stroke-width", "1.45")));
            }
            foreach (var label in draw.Labels)

            {
                group.Add(Text(label));
            }
            root.Add(group);
        }
        foreach (var wire in diagram.Wires)

        {
            if (wire.LabelPosition is { } label)
            {
                var text = Text(new(wire.Label, label.X, label.Y, 11, "middle", 138));
                text.SetAttributeValue("stroke", canvas);
                text.SetAttributeValue("stroke-width", "3");
                text.SetAttributeValue("paint-order", "stroke");
                root.Add(text);
            }
        }
        return new XDocument(root).ToString();

        XElement Text(HdlSchematicGlyph.Label label) => new(ns + "text",
            new XAttribute("x", N(label.X)), new XAttribute("y", N(label.Y)),
            new XAttribute("font-family", "Segoe UI, sans-serif"), new XAttribute("font-size", label.Size), new XAttribute("fill", ink),
            new XAttribute("text-anchor", label.Anchor), new XAttribute("dominant-baseline", "central"),
            Short(label.Text, Math.Max(1, (int)(label.Width / (label.Size * .58)))));
    }

    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Short(string value, int maximum) => value.Length > maximum ? value[..(maximum - 1)] + "…" : value;
}
