namespace StudioX.Application.Output;

/// <summary>工具正在执行的阶段及其实际测量值；不用于判断操作成败。</summary>
public sealed record OutputActivity(string Phase, OutputMeasurement? Measurement = null);
