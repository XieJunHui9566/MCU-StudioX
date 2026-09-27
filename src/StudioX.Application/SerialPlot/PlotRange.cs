namespace StudioX.Application.SerialPlot;

public readonly record struct PlotRange(double Start, double End)
{
    public double Span => End - Start;
    public PlotRange Zoom(double factor, double anchor, double minimumSpan, double maximumSpan)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            return this;
        }
        anchor = double.IsFinite(anchor) ? Math.Clamp(anchor, 0, 1) : .5;
        // 在巨大偏移量处，缩小到浮点精度以下会导致两端重合、绘图除零。
        minimumSpan = Math.Min(maximumSpan, Math.Max(minimumSpan, Math.Max(Math.Abs(Start), Math.Abs(End)) * 2e-15));
        var span = Math.Clamp(Span * factor, minimumSpan, maximumSpan);
        var origin = Start + Span * anchor - span * anchor;
        return new(origin, origin + span);
    }
}
