namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StudioX.Application.SerialPlot;

public sealed class SerialPlotChart : FrameworkElement
{
    public static readonly Brush[] ChannelBrushes = new[] { "#56C7FF", "#FFA85C", "#72E6AA", "#D59FFF", "#FF7D96", "#F4DF70", "#63E6E6", "#B1BBFF",
        "#FFCD9C", "#B5EC8B", "#FFA3EB", "#73A6FF", "#E1AC75", "#BECCD9", "#F38B6D", "#64DBBE" }.Select(s =>
        { var b = (Brush)new BrushConverter().ConvertFromString(s)!; b.Freeze(); return b; }).ToArray();
    private PlotSample[] samples = [];
    private int channels;
    private PlotRange time = new(0, 10), value = new(0, 4096);
    private Point? dragStart, pointer;
    private PlotRange dragTime, dragValue;
    public bool[] VisibleChannels { get; } = Enumerable.Repeat(true, 16).ToArray();
    public bool Follow { get; set; } = true;
    public bool AutoY { get; set; } = true;
    public event Action? ViewChanged;
    private Rect Area => new(82, 28, Math.Max(1, ActualWidth - 104), Math.Max(1, ActualHeight - 82));
    public SerialPlotChart() { Focusable = true; ClipToBounds = true; Cursor = Cursors.Cross; }
    public void SetSamples(PlotSample[] data, int count) { samples = data; channels = count; InvalidateVisual(); }
    public void ResetView() { time = new(0, 10); value = new(0, 4096); Follow = AutoY = true; ViewChanged?.Invoke(); InvalidateVisual(); }
    public void Zoom(bool yAxis, double factor, double anchor = .5)
    {
        if (yAxis) { AutoY = false; value = ClampValue(value.Zoom(factor, anchor, 1e-12, 2e100)); }
        else { time = time.Zoom(factor, anchor, .001, 3600000); Follow = false; }
        ViewChanged?.Invoke(); InvalidateVisual();
    }
    public bool HandleWheel(int delta, bool control)
    {
        if (!IsVisible) return false;
        var p = Mouse.GetPosition(this); var a = Area;
        if (control)
        {
            var y = p.X < a.Left || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            Zoom(y, Math.Pow(.8, Math.Clamp(delta / 120.0, -8, 8)), y ? 1 - (p.Y - a.Top) / a.Height : (p.X - a.Left) / a.Width);
        }
        else
        {
            var shift = -delta / 120.0 * time.Span * .1;
            time = new(time.Start + shift, time.End + shift); Follow = false; ViewChanged?.Invoke(); InvalidateVisual();
        }
        return true;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); Focus();
        if (e.ClickCount == 2) { ResetView(); e.Handled = true; return; }
        dragStart = e.GetPosition(this); dragTime = time; dragValue = value; CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { dragStart = null; ReleaseMouseCapture(); base.OnMouseLeftButtonUp(e); }
    protected override void OnLostMouseCapture(MouseEventArgs e) { dragStart = null; base.OnLostMouseCapture(e); }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); pointer = e.GetPosition(this);
        if (dragStart is Point start)
        {
            var dx = (pointer.Value.X - start.X) / Area.Width * dragTime.Span;
            var dy = (pointer.Value.Y - start.Y) / Area.Height * dragValue.Span;
            time = new(dragTime.Start - dx, dragTime.End - dx); value = ClampValue(new(dragValue.Start + dy, dragValue.End + dy));
            Follow = AutoY = false; ViewChanged?.Invoke();
        }
        InvalidateVisual();
    }
    protected override void OnMouseLeave(MouseEventArgs e) { pointer = null; InvalidateVisual(); base.OnMouseLeave(e); }
    private static PlotRange ClampValue(PlotRange range)
    {
        var span = Math.Min(range.Span, 2.2e100);
        var start = Math.Clamp(range.Start, -1.1e100, 1.1e100 - span);
        return new(start, start + span);
    }
    private Brush Resource(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
    private void Text(DrawingContext dc, string text, Point position, Brush brush, double size = 11)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, position);
    }
    private static string Number(double n) => n.ToString(Math.Abs(n) is >= 1e6 or > 0 and < .001 ? "0.###E+0" : "0.###", CultureInfo.InvariantCulture);
    private static string Tick(double n, double step)
    {
        if (Math.Abs(n) is >= 1e6 or > 0 and < .0001) return n.ToString("0.######E+0", CultureInfo.InvariantCulture);
        var digits = (int)Math.Clamp(Math.Ceiling(-Math.Log10(step)) + 1, 0, 9);
        return n.ToString(digits == 0 ? "0" : "0." + new string('#', digits), CultureInfo.InvariantCulture);
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Resource("EditorSurface"), null, new Rect(RenderSize));
        if (ActualWidth < 150 || ActualHeight < 120) return;
        var a = Area; var muted = Resource("Muted"); var text = Resource("Text");
        if (Follow && samples.Length > 0)
        {
            var end = Math.Max(time.Span, samples[^1].Seconds); time = new(end - time.Span, end);
        }
        if (AutoY)
        {
            var low = double.PositiveInfinity; var high = double.NegativeInfinity;
            foreach (var s in samples)
            {
                if (s.Seconds < time.Start || s.Seconds > time.End) continue;
                for (var c = 0; c < channels; c++) if (VisibleChannels[c]) { low = Math.Min(low, s.Values[c]); high = Math.Max(high, s.Values[c]); }
            }
            if (double.IsFinite(low))
            {
                var pad = Math.Max((high - low) * .1, Math.Max(Math.Abs(high) * .01, 1e-6));
                value = ClampValue(new(low - pad, high + pad));
            }
        }
        double X(double t) => a.Left + (t - time.Start) / time.Span * a.Width;
        double Y(double v) => Math.Clamp(a.Bottom - (v - value.Start) / value.Span * a.Height, -1e6, 1e6);
        var grid = new Pen(Resource("Border"), .6);
        for (var i = 0; i <= 5; i++)
        {
            var y = a.Bottom - a.Height * i / 5;
            dc.DrawLine(grid, new(a.Left, y), new(a.Right, y));
            Text(dc, Tick(value.Start + value.Span * i / 5, value.Span / 5), new(8, y - 8), muted);
        }
        var ticks = Math.Clamp((int)(a.Width / 100), 2, 10);
        for (var i = 0; i <= ticks; i++)
        {
            var x = a.Left + a.Width * i / ticks;
            dc.DrawLine(grid, new(x, a.Top), new(x, a.Bottom));
            Text(dc, Tick(time.Start + time.Span * i / ticks, time.Span / ticks), new(x - 14, a.Bottom + 7), muted);
        }
        dc.DrawRectangle(null, new Pen(muted, .8), a);
        Text(dc, "数值", new(8, 5), text);
        Text(dc, $"时间 (s)    窗宽 {Number(time.Span)} s", new(Math.Max(84, a.Right - 220), a.Bottom + 29), text);
        dc.PushClip(new RectangleGeometry(a));
        for (var c = 0; c < channels; c++)
        {
            if (!VisibleChannels[c]) continue;
            var points = PlotGeometry.Reduce(samples, c, time, (int)a.Width);
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                long previous = -1;
                foreach (var p in points)
                {
                    var point = new Point(X(p.Seconds), Y(p.Value));
                    if (previous != p.Segment) path.BeginFigure(point, false, false);
                    else path.LineTo(point, true, false);
                    previous = p.Segment;
                }
            }
            geometry.Freeze(); dc.DrawGeometry(null, new Pen(ChannelBrushes[c], 1.5), geometry);
            if (points.Count is > 0 and < 100)
                foreach (var p in points) dc.DrawEllipse(ChannelBrushes[c], null, new(X(p.Seconds), Y(p.Value)), 2, 2);
        }
        if (pointer is Point cursor && a.Contains(cursor) && samples.Length > 0)
        {
            var t = time.Start + (cursor.X - a.Left) / a.Width * time.Span;
            var nearest = samples.MinBy(s => Math.Abs(s.Seconds - t))!;
            dc.DrawLine(new Pen(muted, .8) { DashStyle = DashStyles.Dash }, new(X(nearest.Seconds), a.Top), new(X(nearest.Seconds), a.Bottom));
            var label = $"t={Number(nearest.Seconds)} s   " + string.Join("   ", Enumerable.Range(0, channels).Where(c => VisibleChannels[c]).Take(4).Select(c => $"CH{c + 1}={Number(nearest.Values[c])}"));
            dc.DrawRectangle(Resource("ToolSurface"), null, new(a.Left + 7, a.Top + 7, Math.Max(1, a.Width - 14), 25));
            Text(dc, label, new(a.Left + 13, a.Top + 11), text);
        }
        dc.Pop();
        if (samples.Length == 0) Text(dc, "等待数值数据，例如 4095,1024 + 换行", new(a.Left + 24, a.Top + 28), muted, 14);
    }
}
