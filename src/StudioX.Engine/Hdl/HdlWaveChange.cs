namespace StudioX.Engine.Hdl;

/// <summary>原始仿真 tick 上的数字值，保留 x/z。</summary>
public sealed record HdlWaveChange(long Tick, string Value);
