namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StudioX.Engine.Hdl;

public partial class HdlSchematicView
{
    private sealed record SignalEntry(string Module, HdlPort Port);
    private sealed record SignalMatch(SignalEntry Entry, int[]? Positions, string Name)
    {
        public string Label => Name + " · " + Entry.Module;
    }

    private sealed record SignalTrace(string Name, string? Source, HashSet<string> Nets, TraceEndpoint[] Endpoints);
    private sealed record TraceEndpoint(HdlDiagramNode Node, HdlDiagramPin Pin, bool Driver)
    {
        public HashSet<int> Positions { get; } = [];
        public Dictionary<string, HashSet<string>> Connections { get; } = new(StringComparer.Ordinal);
        public string Label => (Driver ? "驱动 · " : "使用 · ") + EndpointName(Node, Pin.Name);
    }

    private const int SignalResultLimit = 40;
    private SignalEntry[] signalCatalog = [];
    private bool changingEndpoint;

    private string? SelectedSource => Surface.Selection switch
    {
        HdlDiagramNode node => node.Source,
        SignalTrace trace => (TraceEndpoints.SelectedItem as TraceEndpoint)?.Node.Source ?? trace.Source,
        _ => null
    };

    private void SetSignalCatalog(HdlSchematicResult? result)
    {
        signalCatalog = result?.Modules.SelectMany(module => module.Ports.Concat(module.Signals)
            .Where(port => !port.IsGenerated)
            .DistinctBy(port => port.Name, StringComparer.Ordinal)
            .Select(port => new SignalEntry(module.Name, port))).ToArray() ?? [];
        SignalSearch.Clear();
        SignalSearch.IsEnabled = result is not null;
        CloseSignalResults();
    }

    private void SignalSearch_Changed(object sender, TextChangedEventArgs e) => FindSignals();

    private void FindSignals()
    {
        var query = SignalSearch.Text.Trim();
        if (query.Length == 0 || Result is null)
        {
            CloseSignalResults();
            return;
        }

        var name = query;
        (int Low, int High)? range = null;
        var bracket = query.LastIndexOf('[');
        if (bracket > 0 && query.EndsWith(']'))
        {
            var parts = query[(bracket + 1)..^1].Split(':');
            if (parts.Length <= 2 && int.TryParse(parts[0], out var first) && int.TryParse(parts[^1], out var last))
            {
                name = query[..bracket].TrimEnd();
                range = (Math.Min(first, last), Math.Max(first, last));
            }
        }

        var matches = signalCatalog
            .Select(entry => (Entry: entry, Rank: entry.Port.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase)))
            .Where(candidate => candidate.Rank >= 0)
            .OrderBy(candidate => candidate.Rank == 0 ? (candidate.Entry.Port.Name.Length == name.Length ? 0 : 1) : 2)
            .ThenByDescending(candidate => candidate.Entry.Module == Surface.Diagram?.ModuleName)
            .ThenBy(candidate => candidate.Entry.Port.Name, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => Match(candidate.Entry, range))
            .OfType<SignalMatch>()
            .Take(SignalResultLimit + 1)
            .ToArray();
        SignalResults.ItemsSource = matches.Take(SignalResultLimit).ToArray();
        SignalResults.SelectedIndex = matches.Length == 0 ? -1 : 0;
        SignalResults.Visibility = matches.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        SignalSearchStatus.Text = matches.Length == 0 ? "未找到匹配信号" : $"仅显示前 {SignalResultLimit} 项，可继续缩小搜索范围";
        SignalSearchStatus.Visibility = matches.Length == 0 || matches.Length > SignalResultLimit
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static SignalMatch? Match(SignalEntry entry, (int Low, int High)? range)
    {
        var port = entry.Port;
        int[]? positions = null;
        if (range is { } bits)
        {
            if (bits.Low < port.Offset || (long)bits.High >= (long)port.Offset + port.Bits.Length)
            {
                return null;
            }
            positions = Enumerable.Range(0, port.Bits.Length)
                .Where(position => bits.Low <= port.IndexAt(position) && port.IndexAt(position) <= bits.High).ToArray();
        }
        return new(entry, positions, SignalName(port, positions));
    }

    private static string SignalName(HdlPort port, int[]? positions)
    {
        if (positions?.Length == 1)
        {
            return $"{port.Name}[{port.IndexAt(positions[0])}]";
        }
        if (port.Bits.Length < 2)
        {
            return port.Name;
        }
        var low = positions?[0] ?? 0;
        var high = positions?[^1] ?? (port.Bits.Length - 1);
        var continuous = positions is null || high - low + 1 == positions.Length;
        return continuous ? $"{port.Name}[{port.IndexAt(high)}:{port.IndexAt(low)}]" : port.Name + "（部分位）";
    }

    private void CloseSignalResults()
    {
        SignalResults.ItemsSource = null;
        SignalResults.Visibility = SignalSearchStatus.Visibility = Visibility.Collapsed;
    }

    private void SignalSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape or Key.Down or Key.Up) || SignalSearch.Text.Length == 0)
        {
            return;
        }
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            CloseSignalResults();
            SignalSearch.Focus();
            return;
        }
        if (SignalResults.Visibility != Visibility.Visible)
        {
            FindSignals();
            return;
        }
        if (e.Key == Key.Enter)
        {
            if (SignalResults.SelectedItem is SignalMatch selected)
            {
                OpenSignal(selected);
            }
            return;
        }
        SignalResults.SelectedIndex = Math.Clamp(SignalResults.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, SignalResults.Items.Count - 1);
        SignalResults.ScrollIntoView(SignalResults.SelectedItem);
    }

    private void SignalResults_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(SignalResults, source) is ListBoxItem { Content: SignalMatch result })
        {
            e.Handled = true;
            OpenSignal(result);
        }
    }

    private void OpenSignal(SignalMatch match)
    {
        CloseSignalResults();
        NavigateModule(match.Entry.Module);
        SelectSignal(match.Entry.Port, match.Positions, match.Name, center: true);
    }

    private void SelectSignal(HdlPort port, int[]? positions, string name, bool center)
    {
        var selectedPositions = positions?.ToHashSet();
        var bits = (positions is null ? port.Bits : positions.Select(position => port.Bits[position]))
            .Where(bit => bit.StartsWith('n')).ToHashSet(StringComparer.Ordinal);
        var terminal = port.Direction == "wire" ? null : "port:" + port.Name;
        Surface.Select(CreateTrace(name, port.Source, (wire, bit) =>
        {
            if (terminal is not null && wire.SourceNode == terminal)

            {
                return selectedPositions is null || selectedPositions.Contains(bit.SourcePosition);
            }
            if (terminal is not null && wire.TargetNode == terminal)

            {
                return selectedPositions is null || selectedPositions.Contains(bit.TargetPosition);
            }
            return bits.Contains(bit.NetBit);
        }, terminal));
        if (center && TraceEndpoints.SelectedItem is TraceEndpoint endpoint)
        {
            CenterEndpoint(endpoint);
        }
    }

    private SignalTrace CreateTrace(string name, string? source, Func<HdlDiagramWire, HdlDiagramBit, bool> matches,
        string? terminal = null)
    {
        var diagram = Surface.Diagram!;
        var nodes = diagram.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var endpoints = new Dictionary<(string Node, string Port, bool Driver), TraceEndpoint>();
        var nets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var wire in diagram.Wires)
        {
            var connection = EndpointName(nodes[wire.SourceNode], wire.SourcePort) + " → " +
                EndpointName(nodes[wire.TargetNode], wire.TargetPort);
            for (var index = 0; index < wire.Bits.Length; index++)
            {
                var bit = wire.Bits[index];
                if (!matches(wire, bit))
                {
                    continue;
                }
                nets.Add(wire.Net);
                Add(wire.SourceNode, wire.SourcePort, true, bit.SourcePosition, connection, wire.BitMappings[index]);
                Add(wire.TargetNode, wire.TargetPort, false, bit.TargetPosition, connection, wire.BitMappings[index]);
            }
        }
        if (endpoints.Count == 0 && terminal is not null && nodes.TryGetValue(terminal, out var node))
        {
            var pin = node.Outputs.FirstOrDefault() ?? node.Inputs[0];
            endpoints.Add((terminal, pin.Name, node.Outputs.Length > 0), new(node, pin, node.Outputs.Length > 0));
        }
        return new(name, source, nets, endpoints.Values.OrderByDescending(endpoint => endpoint.Driver)
            .ThenBy(endpoint => endpoint.Node.Id.StartsWith("port:", StringComparison.Ordinal))
            .ThenBy(endpoint => endpoint.Label, StringComparer.Ordinal).ToArray());

        void Add(string id, string port, bool driver, int position, string connection, string mapping)
        {
            var key = (id, port, driver);
            if (!endpoints.TryGetValue(key, out var endpoint))
            {
                var block = nodes[id];
                var pin = (driver ? block.Outputs : block.Inputs).Single(candidate => candidate.Name == port);
                endpoints.Add(key, endpoint = new(block, pin, driver));
            }
            endpoint.Positions.Add(position);
            if (!endpoint.Connections.TryGetValue(connection, out var mappings))

            {
                endpoint.Connections.Add(connection, mappings = new(StringComparer.Ordinal));
            }
            mappings.Add(mapping);
        }
    }

    private void SelectionChanged(object? value)
    {
        if (value is HdlDiagramWire wire)
        {
            var bits = wire.Bits.Select(bit => bit.NetBit).Where(bit => bit.StartsWith('n')).ToHashSet(StringComparer.Ordinal);
            Surface.Select(CreateTrace(wire.Label, null, (candidate, bit) => candidate.Net == wire.Net || bits.Contains(bit.NetBit)));
            return;
        }
        if (value is HdlDiagramNode node && node.Id.StartsWith("port:", StringComparison.Ordinal) &&
            Result?.Modules.FirstOrDefault(module => module.Name == Surface.Diagram?.ModuleName)?
                .Ports.FirstOrDefault(port => port.Name == node.Label) is { } boundary)
        {
            SelectSignal(boundary, null, SignalName(boundary, null), center: false);
            return;
        }

        TraceEndpoints.ItemsSource = (value as SignalTrace)?.Endpoints;
        TracePanel.Visibility = value is SignalTrace { Endpoints.Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        SetTraceEndpoint((value as SignalTrace)?.Endpoints.FirstOrDefault());
    }

    private void SetTraceEndpoint(TraceEndpoint? endpoint)
    {
        changingEndpoint = true;
        TraceEndpoints.SelectedItem = endpoint;
        changingEndpoint = false;
        UpdateTraceDetails();
    }

    private void UpdateTraceDetails()
    {
        var endpoint = TraceEndpoints.SelectedItem as TraceEndpoint;
        EnterModuleButton.Visibility = ChildPort(endpoint) is not null ? Visibility.Visible : Visibility.Collapsed;
        UpdateSourceButton();
        if (Surface.Selection is SignalTrace trace)
        {
            Surface.SetTraceHighlight(trace.Nets, endpoint?.Pin.Position);
            Details.Text = trace.Name + (endpoint?.Connections.Count > 0
                ? "\n\n" + string.Join("\n\n", endpoint.Connections.Select(connection =>
                    connection.Key + "\n" + string.Join("\n", connection.Value)))
                : "\n\n没有可追踪的连线");
        }
        else
        {
            Details.Text = Surface.Selection is HdlDiagramNode node ? Describe(node)
                : "点选端口或连线查看信号。双击模块进入层级。";
        }
    }

    private void UpdateSourceButton()
    {
        SourceButton.IsEnabled = !stale && SelectedSource is not null;
        SourceButton.ToolTip = stale ? "源码或综合配置已改动，请先重新生成电路图。" : null;
    }

    private void TraceEndpoint_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (changingEndpoint || TraceEndpoints.SelectedItem is not TraceEndpoint endpoint)
        {
            return;
        }
        UpdateTraceDetails();
        CenterEndpoint(endpoint);
    }

    private void CenterEndpoint(TraceEndpoint endpoint)
    {
        ScheduleViewport(() =>
        {
            SetZoom(Math.Max(zoom, 1));
            Viewport.UpdateLayout();
            Viewport.ScrollToHorizontalOffset(endpoint.Pin.Position.X * zoom - Viewport.ViewportWidth / 2);
            Viewport.ScrollToVerticalOffset(endpoint.Pin.Position.Y * zoom - Viewport.ViewportHeight / 2);
        });
    }

    private HdlPort? ChildPort(TraceEndpoint? endpoint) => endpoint is null ? null : Result?.Modules
        .FirstOrDefault(module => module.Name == endpoint.Node.Type)?.Ports.FirstOrDefault(port => port.Name == endpoint.Pin.Name);

    private void EnterModule_Click(object sender, RoutedEventArgs e)
    {
        if (TraceEndpoints.SelectedItem is not TraceEndpoint endpoint || ChildPort(endpoint) is not { } port)
        {
            return;
        }
        var positions = endpoint.Positions.Where(position => position < port.Bits.Length).Order().ToArray();
        var selection = positions.Length > 0 && positions.Length < port.Bits.Length ? positions : null;
        NavigateModule(endpoint.Node.Type);
        SelectSignal(port, selection, SignalName(port, selection), center: true);
    }

    private static string EndpointName(HdlDiagramNode node, string port) =>
        node.Id.StartsWith("port:", StringComparison.Ordinal) ? node.Label : node.Label + "." + port;
}
