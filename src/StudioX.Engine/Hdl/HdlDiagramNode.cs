namespace StudioX.Engine.Hdl;

/// <summary>布局后的单元、模块、常量或边界端口。</summary>
public sealed record HdlDiagramNode(string Id, string Label, string Type, string Symbol, string? Source,
    double X, double Y, double Width, double Height, HdlDiagramPin[] Inputs, HdlDiagramPin[] Outputs);
