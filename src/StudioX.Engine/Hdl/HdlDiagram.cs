namespace StudioX.Engine.Hdl;

/// <summary>可由 WPF 和 SVG 共用的确定性电路图。</summary>
public sealed record HdlDiagram(string ModuleName, double Width, double Height, HdlDiagramNode[] Nodes, HdlDiagramWire[] Wires);
