namespace StudioX.Engine.Hdl;

/// <summary>网表位及其在两端口连接向量中的位置。</summary>
public readonly record struct HdlDiagramBit(string NetBit, int SourcePosition, int TargetPosition);
