namespace StudioX.Engine;

/// <summary>厂商工具确认的封装编号；固定脚不推断电源或晶振名称。</summary>
public sealed record Ag32PackagePin(int Number, bool CanAssign);
