namespace StudioX.Application.Simulation;

/// <summary>模拟绘图数据与两个观察者的累计丢帧量，不代表真实硬件采样。</summary>
public sealed record SimulationPlotFrame(long Generation, long Sequence, double Value, int State, long DroppedFrames);
