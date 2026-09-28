namespace StudioX.Engine.Hdl;

/// <summary>来自仿真 VCD 的数字波形，时间采用原始 tick，未知态与高阻态不归零。</summary>
public sealed record HdlWaveform(decimal NanosecondsPerTick, long EndTick, HdlWaveSignal[] Signals);
