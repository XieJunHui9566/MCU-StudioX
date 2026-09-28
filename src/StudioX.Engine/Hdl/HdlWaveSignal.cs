namespace StudioX.Engine.Hdl;

/// <summary>一条层级信号及其按时间排序的数值变化。</summary>
public sealed record HdlWaveSignal(string Name, int Width, HdlWaveChange[] Changes);
