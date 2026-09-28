namespace StudioX.Engine.Hdl;

/// <summary>综合得到的逻辑单元或子模块实例，保留端口、参数和源码位置。</summary>
public sealed record HdlCell(string Name, string Type, HdlPort[] Ports,
    IReadOnlyDictionary<string, string> Parameters, string? Source);
