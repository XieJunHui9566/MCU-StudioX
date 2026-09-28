namespace StudioX.Engine.Hdl;

/// <summary>按端口对合并的连线；每条线保留实际位映射，不丢弃切片和扇出。</summary>
public sealed record HdlDiagramWire(string SourceNode, string SourcePort, string TargetNode, string TargetPort,
    string Label, string[] BitMappings, HdlPoint[] Points);
