namespace StudioX.Engine.Hdl;

/// <summary>将位级网表合并为总线连线并分层布局；时序反馈单独绕行，不删除环路。</summary>
public static class HdlDiagramLayout
{
    private const double PinGrid = 4;
    private const double MinimumPinSpacing = 12;

    private sealed record Node(string Id, string Label, string Type, string? Source, HdlPort[] Inputs, HdlPort[] Outputs);
    private sealed record End(string Node, HdlPort Port, int Bit);
    private sealed record Link(string Source, string SourcePort, string Target, string TargetPort,
        List<HdlDiagramBit> Bits, List<string> Mappings);

    public static HdlDiagram Create(HdlModule module)
    {
        var nodes = module.Ports.Select(port => new Node("port:" + port.Name, port.Name, port.Direction, port.Source ?? module.Source,
            port.Direction is "output" or "inout" ? [port] : [], port.Direction is "input" or "inout" ? [port] : [])).ToList();
        nodes.AddRange(module.Cells.Select((cell, index) => new Node("cell:" + cell.Name,
            cell.Name.StartsWith('$') ? Symbol(cell.Type) + " #" + (index + 1) : cell.Name, cell.Type, cell.Source,
            cell.Ports.Where(port => port.Direction is "input" or "inout").ToArray(),
            cell.Ports.Where(port => port.Direction is "output" or "inout").ToArray())));
        foreach (var value in nodes.SelectMany(node => node.Inputs).SelectMany(port => port.Bits).Where(bit => !bit.StartsWith('n')).Distinct().ToArray())
        {
            nodes.Add(new("constant:" + value, "1'b" + value, "constant", null, [], [new("Y", "output", [value])]));
        }
        var drivers = new Dictionary<string, List<End>>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            foreach (var port in node.Outputs)
            {
                for (var bit = 0; bit < port.Bits.Length; bit++)
                {
                    if (!drivers.TryGetValue(port.Bits[bit], out var list))
                    {
                        drivers[port.Bits[bit]] = list = [];
                    }
                    list.Add(new(node.Id, port, bit));
                }
            }
        }
        var links = new Dictionary<(string, string, string, string), Link>();
        foreach (var node in nodes)
        {
            foreach (var port in node.Inputs)
            {
                for (var bit = 0; bit < port.Bits.Length; bit++)
                {
                    if (!drivers.TryGetValue(port.Bits[bit], out var sources))
                    {
                        continue;
                    }
                    foreach (var source in sources)
                    {
                        if (source.Node == node.Id && source.Port.Name == port.Name)
                        {
                            continue;
                        }
                        var key = (source.Node, source.Port.Name, node.Id, port.Name);
                        if (!links.TryGetValue(key, out var link))
                        {
                            links[key] = link = new(source.Node, source.Port.Name, node.Id, port.Name, [], []);
                        }
                        link.Bits.Add(new(port.Bits[bit], source.Bit, bit));
                        link.Mappings.Add($"{source.Port.Name}[{source.Port.IndexAt(source.Bit)}] → {port.Name}[{port.IndexAt(bit)}]");
                    }
                }
            }
        }
        var nodesById = nodes.ToDictionary(node => node.Id);
        var forward = links.Values.Where(link => link.Source != link.Target && !IsSequential(nodesById[link.Target].Type)).ToArray();
        var sourcesByTarget = forward.GroupBy(link => link.Target).ToDictionary(group => group.Key, group => group.Select(link => link.Source).Distinct().ToArray());
        var incoming = nodes.ToDictionary(node => node.Id, node => sourcesByTarget.GetValueOrDefault(node.Id) ?? []);
        var outgoing = forward.GroupBy(link => link.Source).ToDictionary(group => group.Key, group => group.Select(link => link.Target).Distinct().ToArray());
        var pending = incoming.ToDictionary(item => item.Key, item => item.Value.Length);
        var ranks = nodes.ToDictionary(node => node.Id, node => IsSequential(node.Type) ? 1 : 0);
        var queue = new Queue<string>(pending.Where(item => item.Value == 0).Select(item => item.Key));
        while (queue.TryDequeue(out var id))
        {
            if (!outgoing.TryGetValue(id, out var children))
            {
                continue;
            }
            foreach (var child in children)
            {
                ranks[child] = Math.Max(ranks[child], ranks[id] + 1);
                if (--pending[child] == 0)
                {
                    queue.Enqueue(child);
                }
            }
        }
        // 组合环路由 Yosys 日志提示；仍呈现全部单元，避免图中悄悄缺失逻辑。
        foreach (var id in pending.Where(item => item.Value > 0).Select(item => item.Key))
        {
            ranks[id] = Math.Max(1, ranks[id]);
        }
        var lastRank = nodes.Where(node => node.Type != "output").Select(node => ranks[node.Id]).DefaultIfEmpty().Max() + 1;
        foreach (var node in nodes.Where(node => node.Type == "output"))
        {
            ranks[node.Id] = lastRank;
        }
        var routed = links.Values.Select(link => new Route(link, ranks[link.Source], ranks[link.Target],
            link.Source + "\u001F" + link.SourcePort + "\u001F" + string.Join(",", link.Bits.Select(bit => bit.NetBit)))).ToArray();
        var tracks = new Dictionary<int, HashSet<(string Net, bool Arrival)>>();
        var feedback = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var route in routed)
        {
            Track(route.FromRank, route.Net, false);
            if (!route.Direct)
            {
                Track(route.ToRank - 1, route.Net, true);
                feedback.TryAdd(route.Net, feedback.Count);
            }
        }
        // 同一列间隙内，反馈线的入线放到出线右侧，避免不同信号在同一行重叠。
        var lanesByRank = tracks.ToDictionary(pair => pair.Key, pair => pair.Value
            .OrderBy(track => track.Arrival).Select((track, index) => (track, index))
            .ToDictionary(item => item.track, item => item.index));

        var sizes = nodes.ToDictionary(node => node.Id, Dimensions);
        var ranksWithNodes = nodes.GroupBy(node => ranks[node.Id]).ToDictionary(group => group.Key, group => group.ToArray());
        var columns = Enumerable.Range(0, ranksWithNodes.Keys.DefaultIfEmpty(0).Max() + 1)
            .Select(rank => (Key: rank, Nodes: ranksWithNodes.GetValueOrDefault(rank) ?? [])).ToArray();
        var widthByRank = columns.ToDictionary(group => group.Key, group => group.Nodes.Select(node => sizes[node.Id].Width).DefaultIfEmpty().Max());
        var rightEdges = new Dictionary<int, double> { [-1] = 30 };
        var columnHeight = columns.ToDictionary(group => group.Key,
            group => group.Nodes.Sum(node => sizes[node.Id].Height) + Math.Max(0, group.Nodes.Length - 1) * 22);
        var totalHeight = columnHeight.Values.DefaultIfEmpty(112).Max();
        var pinOrder = nodes.SelectMany(node => node.Inputs.Select((port, index) => ((node.Id, port.Name), index)))
            .ToDictionary(pair => pair.Item1, pair => pair.index);
        var exitOrder = routed.Where(route => route.Direct)
            .GroupBy(route => route.Link.Source).ToDictionary(group => group.Key,
                group => group.Average(route => pinOrder[(route.Link.Target, route.Link.TargetPort)]));
        var directInputs = routed.Where(route => route.Direct).ToLookup(route => (route.Link.Target, route.Link.TargetPort));
        var placed = new Dictionary<string, HdlDiagramNode>(StringComparer.Ordinal);
        var x = 30d + (tracks.ContainsKey(-1) ? Gap(-1) : 0);
        foreach (var column in columns)
        {
            double y = 44 + (totalHeight - columnHeight[column.Key]) / 2;
            var width = widthByRank[column.Key];
            var ordered = column.Nodes.OrderBy(node => incoming[node.Id].Where(placed.ContainsKey)
                .Select(parent => placed[parent].Y + placed[parent].Height / 2)
                .DefaultIfEmpty(y + exitOrder.GetValueOrDefault(node.Id) * 18).Average())
                .ThenBy(node => node.Id, StringComparer.Ordinal);
            foreach (var node in ordered)
            {
                var size = sizes[node.Id];
                var left = x + (width - size.Width) / 2;
                var shape = HdlSchematicGlyph.For(node.Type);
                var header = shape is HdlSchematicGlyph.Shape.Module or HdlSchematicGlyph.Shape.Memory
                    or HdlSchematicGlyph.Shape.Register or HdlSchematicGlyph.Shape.Latch;
                var portTop = header ? 45 : 14;
                var center = SnapPinY(y + size.Height / 2);
                // 单输入的符号引线位于中心。遇到近邻行时移动整个符号，连接处仍在中心。
                if (!header && node.Inputs.Length == 1)

                {
                    center = AlignInput(node, node.Inputs[0], center);
                }
                var nodeY = center - size.Height / 2;
                HdlDiagramPin[] Pins(HdlPort[] ports, double pinX, bool inputs)
                {
                    double PinY(int index) => ports.Length == 1 && !header ? center
                        : SnapPinY(nodeY + portTop + (size.Height - portTop - 12) * (index + 1) / (ports.Length + 1));
                    var pins = new HdlDiagramPin[ports.Length];
                    for (var index = 0; index < ports.Length; index++)
                    {
                        var port = ports[index];
                        var pinY = PinY(index);
                        if (inputs && (header || ports.Length > 1))
                        {
                            var aligned = AlignInput(node, port, pinY);
                            if ((index == 0 || aligned - pins[index - 1].Position.Y >= MinimumPinSpacing) &&
                                (index == ports.Length - 1 || PinY(index + 1) - aligned >= MinimumPinSpacing))

                            {
                                pinY = aligned;
                            }
                        }
                        pins[index] = new(port.Name, port.Direction, port.Bits, new(pinX, pinY));
                    }
                    return pins;
                }

                placed.Add(node.Id, new(node.Id, node.Label, node.Type, Symbol(node.Type), node.Source,
                    left, nodeY, size.Width, size.Height, Pins(node.Inputs, left, true), Pins(node.Outputs, left + size.Width, false)));
                y += size.Height + 22;
            }
            rightEdges[column.Key] = x + width;
            x += width + Gap(column.Key);
        }

        var bottom = 44 + totalHeight + 32;
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var signal in module.Ports.Concat(module.Signals).Where(signal => !signal.Name.StartsWith('$')))

        {
            names.TryAdd(string.Join(",", signal.Bits), signal.Name);
        }
        var wires = new List<HdlDiagramWire>();
        foreach (var route in routed)
        {
            var link = route.Link;
            var source = placed[link.Source];
            var start = source.Outputs.Single(port => port.Name == link.SourcePort).Position;
            var end = placed[link.Target].Inputs.Single(port => port.Name == link.TargetPort).Position;
            var exitX = Channel(route.FromRank, route.Net, false);
            HdlPoint[] points;
            if (route.Direct)

            {
                points = start.Y == end.Y
                                    ? [start, new(exitX, start.Y), end]
                                    : [start, new(exitX, start.Y), new(exitX, end.Y), end];
            }
            else
            {
                var turnY = bottom + feedback[route.Net] * 22;
                var arrivalX = Channel(route.ToRank - 1, route.Net, true);
                points = [start, new(exitX, start.Y), new(exitX, turnY),
                    new(arrivalX, turnY), new(arrivalX, end.Y), end];
            }
            var name = names.GetValueOrDefault(string.Join(",", link.Bits.Select(bit => bit.NetBit))) ??
                (source.Id.StartsWith("constant:", StringComparison.Ordinal) ? source.Label : link.SourcePort);
            var label = name + (link.Bits.Count > 1 ? $" · {link.Bits.Count} bit" : "");
            wires.Add(new(link.Source, link.SourcePort, link.Target, link.TargetPort, label, link.Mappings.ToArray(), points)
            {
                Net = route.Net,
                Bits = link.Bits.ToArray()
            });
        }

        var junctions = new List<HdlDiagramJunction>();
        var labelRows = new Dictionary<int, List<(double Top, double Left, double Right)>>();
        foreach (var group in wires.Select((wire, index) => (wire, index)).GroupBy(entry => entry.wire.Net))
        {
            var width = group.First().wire.BitMappings.Length;
            var segments = group.SelectMany(entry => entry.wire.Points.Zip(entry.wire.Points.Skip(1),
                (a, b) => (entry.index, A: a, B: b))).Where(segment => segment.A != segment.B).ToArray();
            if (group.Skip(1).Any())
            {
                // 分支端点必须属于同一有序位集合。异网交叉不产生连接点。
                var horizontal = segments.Where(s => s.A.Y == s.B.Y).ToLookup(s => s.A.Y);
                var vertical = segments.Where(s => s.A.X == s.B.X).ToLookup(s => s.A.X);
                foreach (var point in group.SelectMany(entry => entry.wire.Points).Distinct())
                {
                    var alongX = horizontal[point.Y].Where(s => s.A.X <= point.X && point.X <= s.B.X || s.B.X <= point.X && point.X <= s.A.X);
                    var alongY = vertical[point.X].Where(s => s.A.Y <= point.Y && point.Y <= s.B.Y || s.B.Y <= point.Y && point.Y <= s.A.Y);
                    var degree = (alongX.Any(s => Math.Min(s.A.X, s.B.X) < point.X) ? 1 : 0) +
                        (alongX.Any(s => Math.Max(s.A.X, s.B.X) > point.X) ? 1 : 0) +
                        (alongY.Any(s => Math.Min(s.A.Y, s.B.Y) < point.Y) ? 1 : 0) +
                        (alongY.Any(s => Math.Max(s.A.Y, s.B.Y) > point.Y) ? 1 : 0);
                    if (degree >= 3)
                    {
                        junctions.Add(new(group.Key, point, width));
                    }
                }
            }

            var label = group.First().wire.Label;
            var needed = Math.Clamp(label.Length * 7 + 12, 38, 138);
            foreach (var segment in segments.Where(s => s.A.Y == s.B.Y && Math.Abs(s.A.X - s.B.X) >= needed)
                .OrderBy(s => s.A.Y >= bottom).ThenByDescending(s => Math.Abs(s.A.X - s.B.X)))
            {
                var center = (segment.A.X + segment.B.X) / 2;
                var y = segment.A.Y;
                var left = center - needed / 2;
                var right = center + needed / 2;
                var row = (int)(y / 15);
                if (Overlaps(row - 1) || Overlaps(row) || Overlaps(row + 1))
                {
                    continue;
                }
                if (!labelRows.TryGetValue(row, out var occupied))
                {
                    labelRows.Add(row, occupied = []);
                }
                occupied.Add((y, left, right));
                bool Overlaps(int index) => labelRows.TryGetValue(index, out var spans) &&
                    spans.Any(span => Math.Abs(span.Top - y) < 15 && left < span.Right && right > span.Left);
                wires[segment.index] = wires[segment.index] with
                {
                    LabelPosition = new(center, y - 10)
                };
                break;
            }
        }
        HdlDiagramCrossings.SplitHorizontalStrokes(wires);
        var diagramWidth = Math.Max(400, placed.Values.Select(node => node.X + node.Width).DefaultIfEmpty(360).Max() + 30);
        var last = rightEdges.Keys.Max();
        if (tracks.ContainsKey(last))

        {
            diagramWidth = Math.Max(diagramWidth, rightEdges[last] + Gap(last));
        }
        return new(module.Name, diagramWidth, bottom + feedback.Count * 22 + 15, placed.Values.ToArray(), wires.ToArray())
        {
            Junctions = junctions.ToArray()
        };

        double AlignInput(Node node, HdlPort port, double currentY)
        {
            // 同一个输入端可能由多个总线切片拼成。只有源端共用一行时才调整位置。
            var rows = directInputs[(node.Id, port.Name)].Select(route => placed[route.Link.Source].Outputs
                .Single(pin => pin.Name == route.Link.SourcePort).Position.Y).Distinct().Take(2).ToArray();
            return rows.Length == 1 && Math.Abs(rows[0] - currentY) <= PinGrid ? rows[0] : currentY;
        }

        void Track(int rank, string net, bool arrival)
        {
            if (!tracks.TryGetValue(rank, out var gap))
            {
                tracks.Add(rank, gap = []);
            }
            gap.Add((net, arrival));
        }

        double Gap(int rank) => Math.Max(136, 58 + 10 * (tracks.GetValueOrDefault(rank)?.Count ?? 0));

        double Channel(int rank, string net, bool arrival)
        {
            var lanes = lanesByRank[rank];
            return rightEdges[rank] + Gap(rank) / 2 + (lanes[(net, arrival)] - (lanes.Count - 1) / 2d) * 10;
        }
    }

    private static double SnapPinY(double y) => Math.Round(y / PinGrid, MidpointRounding.AwayFromZero) * PinGrid;

    private sealed record Route(Link Link, int FromRank, int ToRank, string Net)
    {
        public bool Direct => ToRank == FromRank + 1;
    }

    private static (double Width, double Height) Dimensions(Node node)
    {
        var shape = HdlSchematicGlyph.For(node.Type);
        var ports = Math.Max(node.Inputs.Length, node.Outputs.Length);
        return shape switch
        {
            HdlSchematicGlyph.Shape.Input or HdlSchematicGlyph.Shape.Output or HdlSchematicGlyph.Shape.Bidirectional
                or HdlSchematicGlyph.Shape.Constant => (Math.Clamp(56 + node.Label.Length * 7 +
                    (node.Inputs.Concat(node.Outputs).Any(p => p.Bits.Length > 1) ? 24 : 0), 94, 208), 38),
            HdlSchematicGlyph.Shape.Module or HdlSchematicGlyph.Shape.Memory => (172, 51 + Math.Max(1, ports) * 19),
            HdlSchematicGlyph.Shape.Register or HdlSchematicGlyph.Shape.Latch => (116, 50 + Math.Max(1, ports) * 19),
            HdlSchematicGlyph.Shape.Mux => (110, Math.Max(96, 36 + ports * 18)),
            _ => (96, Math.Max(76, 33 + ports * 17))
        };
    }

    public static bool IsSequential(string type) => HdlSchematicGlyph.For(type) is
        HdlSchematicGlyph.Shape.Register or HdlSchematicGlyph.Shape.Latch or HdlSchematicGlyph.Shape.Memory;

    public static string Symbol(string type) => type switch
    {
        "$and" or "$logic_and" or "$reduce_and" or "$_AND_" => "AND",
        "$or" or "$logic_or" or "$reduce_or" or "$reduce_bool" or "$_OR_" => "OR",
        "$xor" or "$reduce_xor" or "$_XOR_" => "XOR",
        "$xnor" or "$reduce_xnor" or "$_XNOR_" => "XNOR",
        "$_NAND_" => "NAND",
        "$_NOR_" => "NOR",
        "$not" or "$logic_not" or "$_NOT_" => "NOT",
        "$mux" or "$pmux" or "$bmux" or "$_MUX_" => "MUX",
        "$demux" => "DEMUX",
        "$add" => "+",
        "$sub" => "−",
        "$mul" => "×",
        "$div" => "÷",
        "$mod" => "%",
        "$neg" => "−",
        "$shl" or "$sshl" => "<<",
        "$shr" or "$sshr" => ">>",
        "$eq" => "=",
        "$ne" => "≠",
        "$lt" => "<",
        "$gt" => ">",
        "$le" => "≤",
        "$ge" => "≥",
        "input" => "IN",
        "output" => "OUT",
        "inout" => "I/O",
        "constant" => "CONST",
        _ when HdlSchematicGlyph.For(type) == HdlSchematicGlyph.Shape.Register => "DFF",
        _ when HdlSchematicGlyph.For(type) == HdlSchematicGlyph.Shape.Latch => "LATCH",
        _ when HdlSchematicGlyph.For(type) == HdlSchematicGlyph.Shape.Memory => "MEM",
        _ => type.TrimStart('$')
    };
}
