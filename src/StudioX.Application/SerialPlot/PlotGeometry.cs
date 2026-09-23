namespace StudioX.Application.SerialPlot;

public readonly record struct PlotRange(double Start, double End)
{
    public double Span => End - Start;
    public PlotRange Zoom(double factor, double anchor, double minimumSpan, double maximumSpan)
    {
        if (!double.IsFinite(factor) || factor <= 0) return this;
        anchor = double.IsFinite(anchor) ? Math.Clamp(anchor, 0, 1) : .5;
        // 在巨大偏移量处，缩小到浮点精度以下会导致两端重合、绘图除零。
        minimumSpan = Math.Min(maximumSpan, Math.Max(minimumSpan, Math.Max(Math.Abs(Start), Math.Abs(End)) * 2e-15));
        var span = Math.Clamp(Span * factor, minimumSpan, maximumSpan);
        var origin = Start + Span * anchor - span * anchor;
        return new(origin, origin + span);
    }
}
public readonly record struct PlotPoint(double Seconds, double Value, long Segment);

public static class PlotGeometry
{
    /// <summary>每像素桶保留首、末、极值点及断点；按原始顺序输出，保留窄脉冲。</summary>
    public static IReadOnlyList<PlotPoint> Reduce(IReadOnlyList<PlotSample> source, int channel, PlotRange time, int pixels)
    {
        var output = new List<PlotPoint>();
        if (source.Count == 0 || time.Span <= 0) return output;
        pixels = Math.Clamp(pixels, 1, 4096);
        int first = -1, last = -1, min = -1, max = -1, bucket = int.MinValue;
        long segment = -1;
        void Flush()
        {
            if (first < 0) return;
            Span<int> indices = [first, min, max, last]; indices.Sort();
            var previous = -1;
            foreach (var i in indices)
            {
                if (i == previous) continue;
                previous = i; var s = source[i]; output.Add(new(s.Seconds, s.Values[channel], s.Segment));
            }
        }
        for (var i = 0; i < source.Count; i++)
        {
            var s = source[i];
            // 留边界外各一个点，让线段能连续穿过视口边缘。
            if (s.Seconds < time.Start && i + 1 < source.Count && source[i + 1].Seconds < time.Start) continue;
            if (s.Seconds > time.End && i > 0 && source[i - 1].Seconds > time.End) break;
            var b = (int)Math.Clamp(Math.Floor((s.Seconds - time.Start) / time.Span * pixels), -1, pixels);
            if (first < 0 || b != bucket || segment != s.Segment)
            {
                Flush(); first = last = min = max = i; bucket = b; segment = s.Segment;
            }
            else
            {
                last = i;
                if (s.Values[channel] < source[min].Values[channel]) min = i;
                if (s.Values[channel] > source[max].Values[channel]) max = i;
            }
        }
        Flush(); return output;
    }
}
