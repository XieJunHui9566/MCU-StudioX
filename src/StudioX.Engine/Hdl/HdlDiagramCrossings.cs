namespace StudioX.Engine.Hdl;

/// <summary>在异网交叉处给横线留缺口，逻辑路径仍完整供命中与位映射使用。</summary>
internal static class HdlDiagramCrossings
{
    private sealed record Vertical(string Net, double X, double MinY, double MaxY);

    internal static void SplitHorizontalStrokes(List<HdlDiagramWire> wires)
    {
        // 大图保持常规原理图规则，异网交叉无连接点。
        if (wires.Sum(wire => (long)wire.Points.Length) > 12000)
        {
            return;
        }
        var verticals = wires.SelectMany(wire => wire.Points.Zip(wire.Points.Skip(1))
            .Where(pair => pair.First.X == pair.Second.X && pair.First.Y != pair.Second.Y)
            .Select(pair => new Vertical(wire.Net, pair.First.X, Math.Min(pair.First.Y, pair.Second.Y), Math.Max(pair.First.Y, pair.Second.Y))))
            .Distinct().GroupBy(vertical => vertical.X).OrderBy(group => group.Key).ToArray();
        var coordinates = verticals.Select(group => group.Key).ToArray();
        for (var i = 0; i < wires.Count; i++)
        {
            var wire = wires[i];
            var strokes = new List<HdlPoint[]>();
            var current = new List<HdlPoint> { wire.Points[0] };
            foreach (var (from, to) in wire.Points.Zip(wire.Points.Skip(1)))
            {
                if (from.Y == to.Y && Math.Abs(from.X - to.X) > 12)
                {
                    var left = Math.Min(from.X, to.X) + 6;
                    var right = Math.Max(from.X, to.X) - 6;
                    var first = Array.BinarySearch(coordinates, left);
                    first = first >= 0 ? first : ~first;
                    var cuts = new List<double>();
                    for (var v = first; v < verticals.Length && coordinates[v] < right; v++)

                    {
                        if (verticals[v].Any(line => line.Net != wire.Net && line.MinY < from.Y && from.Y < line.MaxY))

                        {
                            cuts.Add(coordinates[v]);
                        }
                    }
                    if (from.X > to.X)
                    {
                        cuts.Reverse();
                    }
                    var direction = Math.Sign(to.X - from.X);
                    var edge = from.X;
                    foreach (var cut in cuts)
                    {
                        var before = cut - direction * 4;
                        if ((before - edge) * direction > 0)
                        {
                            current.Add(new(before, from.Y));
                            strokes.Add(current.ToArray());
                            current.Clear();
                        }
                        edge = cut + direction * 4;
                        if (current.Count == 0)
                        {
                            current.Add(new(edge, from.Y));
                        }
                        else
                        {
                            current[^1] = new(edge, from.Y);
                        }
                    }
                }
                if (current[^1] != to)
                {
                    current.Add(to);
                }
            }
            if (strokes.Count == 0)
            {
                continue;
            }
            strokes.Add(current.ToArray());
            wires[i] = wire with
            {
                Strokes = strokes.ToArray()
            };
        }
    }
}
