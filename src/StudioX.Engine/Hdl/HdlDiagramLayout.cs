namespace StudioX.Engine.Hdl;

/// <summary>将位级网表合并为总线连线并分层布局；时序反馈单独绕行，不删除环路。</summary>
public static class HdlDiagramLayout
{
    private sealed record Node(string Id, string Label, string Type, string? Source, HdlPort[] Inputs, HdlPort[] Outputs);
    private sealed record End(string Node, HdlPort Port, int Bit);
    private sealed record Link(string Source, string SourcePort, string Target, string TargetPort, List<string> Bits, List<string> Mappings);

    public static HdlDiagram Create(HdlModule module)
    {
        var nodes = module.Ports.Select(port => new Node("port:" + port.Name, port.Name, port.Direction, module.Source,
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
                        link.Bits.Add(port.Bits[bit]);
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
        var placed = new Dictionary<string, HdlDiagramNode>(StringComparer.Ordinal);
        foreach (var column in nodes.GroupBy(node => ranks[node.Id]).OrderBy(group => group.Key))
        {
            double y = 52;
            var ordered = column.OrderBy(node => incoming[node.Id].Where(placed.ContainsKey)
                .Select(parent => placed[parent].Y).DefaultIfEmpty(y).Average()).ThenBy(node => node.Id, StringComparer.Ordinal);
            foreach (var node in ordered)
            {
                var width = 180d;
                var terminal = node.Type is "input" or "output" or "inout" or "constant";
                var height = terminal ? 58 : 64 + Math.Max(node.Inputs.Length, node.Outputs.Length) * 24d;
                var x = 45 + column.Key * 270d;
                var pinStart = terminal ? 42 : 57;
                var inputs = node.Inputs.Select((port, index) => new HdlDiagramPin(port.Name, port.Direction, port.Bits, new(x, y + pinStart + index * 24))).ToArray();
                var outputs = node.Outputs.Select((port, index) => new HdlDiagramPin(port.Name, port.Direction, port.Bits, new(x + width, y + pinStart + index * 24))).ToArray();
                placed.Add(node.Id, new(node.Id, node.Label, node.Type, Symbol(node.Type), node.Source, x, y, width, height, inputs, outputs));
                y += height + 26;
            }
        }
        var bottom = placed.Values.Select(node => node.Y + node.Height).DefaultIfEmpty(100).Max() + 45;
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var signal in module.Signals)
        {
            aliases.TryAdd(string.Join(",", signal.Bits), signal.Name);
        }
        var wires = new List<HdlDiagramWire>();
        var feedback = 0;
        foreach (var link in links.Values)
        {
            var start = placed[link.Source].Outputs.Single(port => port.Name == link.SourcePort).Position;
            var end = placed[link.Target].Inputs.Single(port => port.Name == link.TargetPort).Position;
            HdlPoint[] points;
            if (end.X > start.X && end.X - start.X < 200)
            {
                var channel = (start.X + end.X) / 2 + (wires.Count % 5 - 2) * 7;
                points = [start, new(channel, start.Y), new(channel, end.Y), end];
            }
            else
            {
                var channelY = bottom + feedback++ * 24;
                points = [start, new(start.X + 25, start.Y), new(start.X + 25, channelY), new(end.X - 25, channelY), new(end.X - 25, end.Y), end];
            }
            var signalName = aliases.GetValueOrDefault(string.Join(",", link.Bits)) ?? link.SourcePort;
            var label = signalName + (link.Bits.Count > 1 ? $" [{link.Bits.Count} bit]" : "");
            wires.Add(new(link.Source, link.SourcePort, link.Target, link.TargetPort, label, link.Mappings.ToArray(), points));
        }
        return new(module.Name, placed.Values.Select(node => node.X + node.Width).DefaultIfEmpty(400).Max() + 55,
            bottom + feedback * 24 + 30, placed.Values.ToArray(), wires.ToArray());
    }

    public static bool IsSequential(string type) => type.Contains("dff", StringComparison.OrdinalIgnoreCase)
        || type.Contains("latch", StringComparison.OrdinalIgnoreCase) || type.StartsWith("$mem", StringComparison.Ordinal);

    public static string Symbol(string type) => type switch
    {
        "$and" or "$logic_and" or "$_AND_" => "&",
        "$or" or "$logic_or" or "$_OR_" => "≥1",
        "$xor" or "$_XOR_" => "=1",
        "$not" or "$logic_not" or "$_NOT_" => "NOT",
        "$mux" or "$pmux" => "MUX",
        "$add" => "+",
        "$sub" => "−",
        "$mul" => "×",
        "$div" => "÷",
        "$eq" => "=",
        "$ne" => "≠",
        "$lt" => "<",
        "$gt" => ">",
        "input" => "IN",
        "output" => "OUT",
        "inout" => "I/O",
        "constant" => "CONST",
        _ when type.Contains("dff", StringComparison.OrdinalIgnoreCase) => "DFF",
        _ when type.Contains("latch", StringComparison.OrdinalIgnoreCase) => "LATCH",
        _ when type.StartsWith("$mem", StringComparison.Ordinal) => "MEM",
        _ => type.TrimStart('$')
    };
}
