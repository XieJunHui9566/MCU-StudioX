namespace StudioX.Engine.Hdl;

using System.Globalization;

/// <summary>电路符号的路径和文字锚点由屏幕与 SVG 共用。</summary>
public static class HdlSchematicGlyph
{
    public sealed record Path(string Data, bool Filled = false);
    public sealed record Label(string Text, double X, double Y, int Size, string Anchor = "start", double Width = 160);
    public sealed record Drawing(Path[] Paths, Label[] Labels);

    public enum Shape
    {
        Module, Input, Output, Bidirectional, Constant, And, Or, Xor, Not, Nand, Nor, Xnor, Mux, Register, Latch, Memory, Operator
    }

    public static Shape For(string type) => type switch
    {
        "input" => Shape.Input,
        "output" => Shape.Output,
        "inout" => Shape.Bidirectional,
        "constant" => Shape.Constant,
        "$and" or "$logic_and" or "$reduce_and" or "$_AND_" => Shape.And,
        "$or" or "$logic_or" or "$reduce_or" or "$reduce_bool" or "$_OR_" => Shape.Or,
        "$xor" or "$reduce_xor" or "$_XOR_" => Shape.Xor,
        "$xnor" or "$reduce_xnor" or "$_XNOR_" => Shape.Xnor,
        "$_NAND_" => Shape.Nand,
        "$_NOR_" => Shape.Nor,
        "$not" or "$logic_not" or "$_NOT_" => Shape.Not,
        "$mux" or "$pmux" or "$bmux" or "$demux" or "$_MUX_" => Shape.Mux,
        "$add" or "$sub" or "$mul" or "$div" or "$mod" or "$neg" or "$eq" or "$eqx" or "$ne" or "$nex"
            or "$lt" or "$le" or "$gt" or "$ge" or "$shl" or "$shr" or "$sshl" or "$sshr" or "$shift" or "$shiftx" => Shape.Operator,
        "$dff" or "$dffe" or "$adff" or "$adffe" or "$sdff" or "$sdffe" or "$sdffce" or "$aldff" or "$aldffe" or "$dffsr" or "$dffsre" => Shape.Register,
        "$dlatch" or "$dlatchsr" or "$adlatch" => Shape.Latch,
        "$mem" or "$mem_v2" or "$memrd" or "$memrd_v2" or "$memwr" or "$memwr_v2" => Shape.Memory,
        _ when type.StartsWith("$_", StringComparison.Ordinal) && type.Contains("DFF", StringComparison.Ordinal) => Shape.Register,
        _ when type.StartsWith("$_DLATCH", StringComparison.Ordinal) => Shape.Latch,
        _ => Shape.Module
    };

    public static Drawing Create(HdlDiagramNode node)
    {
        var paths = new List<Path>();
        var labels = new List<Label>();
        var shape = For(node.Type);
        // 绘图路径使用相对元件左上角的布局坐标，连接点仍属于同一个网表。
        var w = node.Width;
        var h = node.Height;
        var cy = h / 2;
        var left = 14d;
        var right = w - 14;
        var top = 10d;
        var bottom = h - 10;
        var curved = shape is Shape.Or or Shape.Xor or Shape.Nor or Shape.Xnor;
        var inverted = shape is Shape.Not or Shape.Nand or Shape.Nor or Shape.Xnor;
        var terminal = shape is Shape.Input or Shape.Output or Shape.Bidirectional or Shape.Constant;
        if (terminal)
        {
            top = 5;
            bottom = h - 5;
            var leftEdge = shape == Shape.Bidirectional ? left - 4 : shape == Shape.Output ? left + 13 : left + 8;
            var pointRight = shape != Shape.Constant;
            paths.Add(new($"M {P(left + 8, top)} L {P(right - 8, top)} L {P(pointRight ? right + 4 : right - 8, cy)} L {P(right - 8, bottom)} L {P(left + 8, bottom)} L {P(leftEdge, cy)} Z", true));
            if (node.Inputs.Length != 0)
            {
                Line(0, cy, leftEdge, cy);
            }
            if (node.Outputs.Length != 0)
            {
                Line(pointRight ? right + 4 : right - 8, cy, w, cy);
            }
            var port = node.Inputs.Concat(node.Outputs).FirstOrDefault();
            var text = node.Label + (port is { Bits.Length: > 1 } ? $" [{port.Bits.Length}]" : "");
            labels.Add(new(text, w / 2, cy, 13, "middle", w - 30));
            return new(paths.ToArray(), labels.ToArray());
        }

        var tip = inverted ? right - 9 : right;
        if (shape is Shape.And or Shape.Nand)
        {
            var shoulder = (left + tip) / 2;
            paths.Add(new($"M {P(left, top)} L {P(shoulder, top)} C {P(tip, top)} {P(tip, top + 12)} {P(tip, cy)} C {P(tip, bottom - 12)} {P(tip, bottom)} {P(shoulder, bottom)} L {P(left, bottom)} Z", true));
        }
        else if (curved)
        {
            paths.Add(new($"M {P(left, top)} Q {P(tip - 18, top - 3)} {P(tip, cy)} Q {P(tip - 18, bottom + 3)} {P(left, bottom)} Q {P(left + 24, cy)} {P(left, top)} Z", true));
            if (shape is Shape.Xor or Shape.Xnor)

            {
                paths.Add(new($"M {P(left - 6, top)} Q {P(left + 18, cy)} {P(left - 6, bottom)}"));
            }
        }
        else if (shape == Shape.Not)
        {
            paths.Add(new($"M {P(left, top)} L {P(tip, cy)} L {P(left, bottom)} Z", true));
        }
        else if (shape == Shape.Mux)
        {
            paths.Add(new($"M {P(left, top)} L {P(right, top + 15)} L {P(right, bottom - 15)} L {P(left, bottom)} Z", true));
        }
        else if (shape == Shape.Operator)
        {
            paths.Add(new($"M {P(left, top)} L {P(right - 11, top)} L {P(right, top + 12)} L {P(right, bottom - 12)} L {P(right - 11, bottom)} L {P(left, bottom)} L {P(left + 7, cy)} Z", true));
        }
        else
        {
            paths.Add(new($"M {P(left, top)} H {N(right)} V {N(bottom)} H {N(left)} Z", true));
            if (shape is Shape.Module or Shape.Memory)

            {
                Line(left, top + 25, right, top + 25);
            }
        }

        if (inverted)

        {
            paths.Add(new($"M {P(tip, cy)} a 4.5 4.5 0 1 0 9 0 a 4.5 4.5 0 1 0 -9 0 Z", true));
        }

        bool block = shape is Shape.Module or Shape.Memory;
        if (block)
        {
            var name = shape == Shape.Memory ? "MEM · " + node.Label : node.Label;
            labels.Add(new(name, w / 2, top + 13, 13, "middle", w - 40));
        }
        else if (shape != Shape.Not)
        {
            var symbol = node.Symbol;
            var symbolX = curved ? w * .57 : w * .54;
            labels.Add(new(symbol, symbolX, shape is Shape.Register or Shape.Latch ? top + 14 : cy - (shape == Shape.Mux ? 5 : 0),
                shape == Shape.Operator ? 22 : 14, "middle", w * .45));
        }

        foreach (var pin in node.Inputs)
        {
            var y = pin.Position.Y - node.Y;
            var edge = curved ? left + 12 * (1 - Math.Pow((y - cy) / (cy - top), 2)) : shape == Shape.Operator ? left + 7 * (1 - Math.Abs(y - cy) / (cy - top)) : left;
            Line(0, y, edge, y);
            if (shape == Shape.Register && pin.Name is "CLK" or "C")
            {
                paths.Add(new($"M {P(left, y - 5)} L {P(left + 8, y)} L {P(left, y + 5)}"));
                continue;
            }
            if (block || shape is Shape.Mux or Shape.Register or Shape.Latch)
            {
                var name = node.Type is "$mux" or "$_MUX_" ? pin.Name switch
                {
                    "A" => "0",
                    "B" => "1",
                    _ => pin.Name
                } : pin.Name;
                labels.Add(new(name, left + 6, y, 11, "start", w * .38));
            }
        }
        foreach (var pin in node.Outputs)
        {
            var y = pin.Position.Y - node.Y;
            Line(right, y, w, y);
            if (block || shape is Shape.Register or Shape.Latch)

            {
                labels.Add(new(pin.Name, right - 6, y, 11, "end", w * .38));
            }
        }
        return new(paths.ToArray(), labels.ToArray());

        void Line(double x, double y, double endX, double endY) => paths.Add(new($"M {P(x, y)} L {P(endX, endY)}"));
    }

    private static string N(double number) => number.ToString("0.###", CultureInfo.InvariantCulture);
    private static string P(double x, double y) => N(x) + " " + N(y);
}
