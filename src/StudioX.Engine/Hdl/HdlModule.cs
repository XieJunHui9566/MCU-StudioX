namespace StudioX.Engine.Hdl;

/// <summary>一个模块的逻辑网络；命名信号和单元共享 Yosys 位标识。</summary>
public sealed record HdlModule(string Name, HdlPort[] Ports, HdlCell[] Cells,
    HdlPort[] Signals, string? Source, bool IsTop, bool IsBlackBox);
