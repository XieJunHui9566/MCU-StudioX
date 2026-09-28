namespace StudioX.Engine.Hdl;

/// <summary>逻辑单元端口在图上的连接点。</summary>
public sealed record HdlDiagramPin(string Name, string Direction, string[] Bits, HdlPoint Position);
