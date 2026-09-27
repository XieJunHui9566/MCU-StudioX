namespace StudioX.Application.Simulation;

/// <summary>日志观察者收到的模拟帧；代次用于丢弃上一连接迟到的界面回调。</summary>
public sealed record SimulationLogFrame(long Generation, long Sequence, string Text);
