namespace StudioX.Engine;

/// <summary>一个分配冲突及其涉及的所有引脚，供图形标记和保存诊断共用。</summary>
public sealed record Ag32PinPlanConflict(string Kind, string Message, int[] PinNumbers, string[] Functions);
