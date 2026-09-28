namespace StudioX.Engine.Hdl;

using System.Globalization;
using System.Xml.Linq;

/// <summary>导出自包含 SVG；源码标识符只作为转义后的文本，不执行脚本或加载外部资源。</summary>
public static class HdlDiagramSvg
{
    public static string Write(HdlDiagram diagram)
    {
        XNamespace ns = "http://www.w3.org/2000/svg";
        var root = new XElement(ns + "svg", new XAttribute("viewBox", $"0 0 {N(diagram.Width)} {N(diagram.Height)}"),
            new XAttribute("width", N(diagram.Width)), new XAttribute("height", N(diagram.Height)),
            new XElement(ns + "rect", new XAttribute("width", "100%"), new XAttribute("height", "100%"), new XAttribute("fill", "#141b26")));
        void Text(double x, double y, string value, int size = 12, string color = "#dce6f3", string anchor = "start") =>
            root.Add(new XElement(ns + "text", new XAttribute("x", N(x)), new XAttribute("y", N(y)),
                new XAttribute("font-family", "Consolas,monospace"), new XAttribute("font-size", size), new XAttribute("fill", color),
                new XAttribute("text-anchor", anchor), value));
        Text(24, 27, diagram.ModuleName, 17);
        foreach (var wire in diagram.Wires)
        {
            root.Add(new XElement(ns + "polyline", new XAttribute("points", string.Join(" ", wire.Points.Select(point => N(point.X) + "," + N(point.Y)))),
                new XAttribute("fill", "none"), new XAttribute("stroke", "#82b4cc"), new XAttribute("stroke-width", wire.BitMappings.Length > 1 ? 2 : 1),
                new XElement(ns + "title", wire.Label + "\n" + string.Join("\n", wire.BitMappings))));
        }
        foreach (var node in diagram.Nodes)
        {
            root.Add(new XElement(ns + "rect", new XAttribute("x", N(node.X)), new XAttribute("y", N(node.Y)),
                new XAttribute("width", N(node.Width)), new XAttribute("height", N(node.Height)), new XAttribute("rx", 5),
                new XAttribute("fill", "#243349"), new XAttribute("stroke", "#7295bd")));
            Text(node.X + 12, node.Y + 22, Short(node.Label, 18), 14);
            if (node.Type is not ("input" or "output" or "inout" or "constant"))
            {
                Text(node.X + 12, node.Y + 39, Short(node.Symbol, 20), 12, "#8fdcb8");
            }
            foreach (var pin in node.Inputs.Concat(node.Outputs))
            {
                var left = pin.Position.X == node.X;
                Text(pin.Position.X + (left ? 9 : -9), pin.Position.Y + 4,
                    Short(pin.Name, 8) + (pin.Bits.Length > 1 ? $" [{pin.Bits.Length}]" : ""), 11, "#dce6f3", left ? "start" : "end");
                root.Add(new XElement(ns + "circle", new XAttribute("cx", N(pin.Position.X)), new XAttribute("cy", N(pin.Position.Y)),
                    new XAttribute("r", 3), new XAttribute("fill", "#82b4cc")));
            }
        }
        return new XDocument(root).ToString();
    }

    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Short(string value, int maximum) => value.Length > maximum ? value[..(maximum - 1)] + "…" : value;
}
