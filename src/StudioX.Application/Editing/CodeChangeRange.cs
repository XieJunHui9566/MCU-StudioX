namespace StudioX.Application.Editing;

/// <summary>两个只读快照之间的对应变化区域；零长度表示插入或删除的锚点。</summary>
public sealed record CodeChangeRange(int BeforeStart, int BeforeLength, int AfterStart, int AfterLength);
