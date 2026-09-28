namespace StudioX.Desktop;

/// <summary>
/// 封装图的单个引脚呈现数据；可分配性与已绑定功能由工程映射服务提供。
/// </summary>
public sealed record Ag32PackagePinVisual(int Number, bool CanAssign, string? Function, string? Conflict = null);
