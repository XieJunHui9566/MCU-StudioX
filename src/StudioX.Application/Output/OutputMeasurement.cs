namespace StudioX.Application.Output;

/// <summary>工具实际报告的阶段进度；不表示整个构建的完成比例。</summary>
public sealed record OutputMeasurement(long Completed, long Total, bool PercentageOnly = false)
{
    public double Percent => Completed * 100d / Total;
    public string Detail => PercentageOnly ? $"{Percent:0}%" : $"{Completed:N0} / {Total:N0} · {Percent:0}%";
}
