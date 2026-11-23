namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using StudioX.Engine.Hdl;

/// <summary>在可见时间窗口内绘制 VCD，时间轴和信号名称不随对应方向的滚动消失。</summary>
public sealed class HdlWaveformSurface : FrameworkElement
{
    private const double RulerHeight = 34;
    private const double RowHeight = 34;
    private static readonly Brush PlotBackground = new SolidColorBrush(Color.FromRgb(20, 27, 39));
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromRgb(55, 65, 80)), 1);
    private static readonly Pen LevelPen = new(Brushes.LightGreen, 1.4);
    private static readonly Pen UnknownPen = new(Brushes.OrangeRed, 1.4);
    private static readonly Pen ImpedancePen = new(Brushes.Gold, 1.4);
    private static readonly Pen CursorPen = new(Brushes.DeepSkyBlue, 1);
    private static readonly Typeface WaveFont = new("Consolas");
    private readonly DrawingVisual cursorLayer = new();
    private readonly Dictionary<(string, Brush, double), DrawingGroup> labelDrawings = new();
    private double textSize;
    private double pixelsPerDip;

    private HdlWaveform? waveform;
    private HdlWaveSignal[] visible = [];
    private long? cursor;
    private bool draggingCursor;
    private int draggedSignalRow;
    private double verticalOffset;
    private double viewportHeight;
    private double PlotFontSize => (double)GetValue(TextElement.FontSizeProperty);
    private double LabelWidth => Math.Min(ActualWidth * .40, Math.Max(260, PlotFontSize * 26));
    private double PlotWidth => Math.Max(1, ActualWidth - LabelWidth);
    internal long StartTick
    {
        get; private set;
    }
    internal long SpanTicks
    {
        get; private set;
    }
    internal decimal CursorFraction => cursor is long tick && SpanTicks > 0 && tick >= StartTick && tick <= StartTick + SpanTicks
        ? (decimal)(tick - StartTick) / SpanTicks : .5m;
    public event Action<string>? CursorChanged;

    public HdlWaveformSurface() => AddVisualChild(cursorLayer);
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? cursorLayer : throw new ArgumentOutOfRangeException(nameof(index));

    internal void SetWaveform(HdlWaveform? value, string filter)
    {
        waveform = value;
        cursor = null;
        SetTimeWindow(0, value?.EndTick ?? 0);
        Filter(filter);
    }

    internal void Filter(string text)
    {
        StopCursorDrag();
        visible = waveform?.Signals.Where(signal => signal.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(128).ToArray() ?? [];
        MinHeight = RulerHeight + Math.Max(1, visible.Length) * RowHeight;
        InvalidateVisual();
    }

    internal void SetTimeWindow(long start, long span)
    {
        StartTick = start;
        SpanTicks = span;
        InvalidateVisual();
    }

    internal void SetVerticalViewport(double offset, double height)
    {
        verticalOffset = offset;
        viewportHeight = height;
        InvalidateVisual();
    }

    internal decimal FractionAt(double x) => (decimal)Math.Clamp((x - LabelWidth) / PlotWidth, 0, 1);
    private double X(long tick) => LabelWidth + (SpanTicks == 0 ? 0 : (double)(tick - StartTick) / SpanTicks * PlotWidth);

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        var position = e.GetPosition(this);
        if (waveform is null || e.ChangedButton != MouseButton.Left || position.X < LabelWidth)
        {
            return;
        }
        draggedSignalRow = position.Y < verticalOffset + RulerHeight ? -1 : (int)((position.Y - RulerHeight) / RowHeight);
        MoveCursor(position, draggedSignalRow);
        draggingCursor = CaptureMouse();
        Cursor = Cursors.SizeWE;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        if (draggingCursor && e.LeftButton == MouseButtonState.Pressed)
        {
            MoveCursor(position, draggedSignalRow);
            e.Handled = true;
        }
        else if (draggingCursor)
        {
            StopCursorDrag();
        }
        else if (cursor is long tick && position.X >= LabelWidth && Math.Abs(position.X - X(tick)) <= 6)
        {
            Cursor = Cursors.SizeWE;
        }
        else
        {
            ClearValue(CursorProperty);
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (draggingCursor && e.ChangedButton == MouseButton.Left)
        {
            MoveCursor(e.GetPosition(this), draggedSignalRow);
            StopCursorDrag();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        draggingCursor = false;
        ClearValue(CursorProperty);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!draggingCursor)
        {
            ClearValue(CursorProperty);
        }
    }

    private void StopCursorDrag()
    {
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        draggingCursor = false;
        ClearValue(CursorProperty);
    }

    internal void MoveCursor(Point position, int row)
    {
        if (waveform is null)
        {
            return;
        }
        var tick = StartTick + (long)decimal.Round(FractionAt(position.X) * SpanTicks);
        cursor = tick;
        var sample = row >= 0 && row < visible.Length ? visible[row] : null;
        CursorChanged?.Invoke("光标 " + FormatTime(tick * waveform.NanosecondsPerTick) + (sample is null
            ? "" : $" · {sample.Name} [{sample.Width}] = {ValueAt(sample, tick)}"));
        DrawCursorLayer();
    }

    // VCD 已按 tick 排序。保留窗口左边界之前的最后一个值，长记录平移时只遍历可见段。
    private static int LastChangeAt(HdlWaveChange[] changes, long tick)
    {
        var low = 0;
        var high = changes.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (changes[middle].Tick <= tick)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low - 1;
    }

    private static string ValueAt(HdlWaveSignal signal, long tick)
    {
        var index = LastChangeAt(signal.Changes, tick);
        return index < 0 ? "x" : signal.Changes[index].Value;
    }

    internal static string FormatTime(decimal ns)
    {
        var (unit, label) = ns switch
        {
            >= 1_000_000_000 => (1_000_000_000m, "s"),
            >= 1_000_000 => (1_000_000m, "ms"),
            >= 1_000 => (1_000m, "µs"),
            >= 1 or 0 => (1m, "ns"),
            >= .001m => (.001m, "ps"),
            _ => (.000001m, "fs")
        };
        return $"{ns / unit:0.###############} {label}";
    }

    protected override void OnRender(DrawingContext context)
    {
        var bottom = Math.Min(ActualHeight, verticalOffset + viewportHeight);
        context.DrawRectangle(PlotBackground, null, new Rect(RenderSize));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (textSize != PlotFontSize || pixelsPerDip != dpi)
        {
            textSize = PlotFontSize;
            pixelsPerDip = dpi;
            labelDrawings.Clear();
        }
        DrawCursorLayer();
        if (waveform is null || bottom <= verticalOffset || ActualWidth < 4)
        {
            return;
        }

        var first = Math.Max(0, (int)(verticalOffset / RowHeight));
        var last = Math.Min(visible.Length, (int)Math.Ceiling((bottom - RulerHeight) / RowHeight));
        for (var row = first; row < last; row++)
        {
            var signal = visible[row];
            var y = RulerHeight + row * RowHeight + 4;
            var valueWidth = Math.Min(65, LabelWidth / 3);
            DrawText(context, signal.Name, 6, y, Brushes.WhiteSmoke, LabelWidth - valueWidth - 10);
            context.DrawLine(GridPen, new(0, y + 29), new(ActualWidth, y + 29));
            DrawSignal(context, signal, y);
        }
        if (visible.Length == 0)
        {
            DrawText(context, "没有匹配的信号", 8, RulerHeight + 6, Brushes.LightGray, ActualWidth - 16);
        }
        // 标尺固定在垂直视口顶部；仅刻度文字按可用宽度避让，网格始终对应所选时基。
        context.DrawRectangle(PlotBackground, null, new Rect(0, verticalOffset, ActualWidth, RulerHeight));
        DrawText(context, "信号", 6, verticalOffset + 6, Brushes.LightGray, LabelWidth / 2);
        DrawText(context, "值", LabelWidth - Math.Min(65, LabelWidth / 3), verticalOffset + 6, Brushes.LightGray, LabelWidth / 3);
        var end = StartTick + SpanTicks;
        var endLabel = FormatTime(end * waveform.NanosecondsPerTick);
        var endWidth = Math.Min(PlotWidth / 2, MakeText(endLabel, Brushes.LightGray).Width + 2);
        var nextLabelX = LabelWidth;
        var divisions = (int)Math.Min(10, SpanTicks);
        for (var i = 0; i <= divisions; i++)
        {
            var tick = StartTick + (divisions == 0 ? 0 : (long)((decimal)SpanTicks * i / divisions));
            var x = X(tick);
            context.DrawLine(GridPen, new(x, verticalOffset + RulerHeight - 5), new(x, bottom));
            if (i == divisions && divisions > 0)
            {
                DrawText(context, endLabel, ActualWidth - endWidth, verticalOffset + 6, Brushes.LightGray, endWidth);
                break;
            }
            var label = FormatTime(tick * waveform.NanosecondsPerTick);
            var width = MakeText(label, Brushes.LightGray).Width + 2;
            if (x >= nextLabelX && x + width + 6 < ActualWidth - endWidth)
            {
                DrawText(context, label, x + 2, verticalOffset + 6, Brushes.LightGray, width);
                nextLabelX = x + width + 8;
            }
        }
    }

    private void DrawCursorLayer()
    {
        // 独立保留光标和值列；移动光标时不重新提交波形路径、标尺或信号名。
        using var context = cursorLayer.RenderOpen();
        var bottom = Math.Min(ActualHeight, verticalOffset + viewportHeight);
        var top = verticalOffset + RulerHeight;
        if (waveform is null || bottom <= top || textSize <= 0)
        {
            return;
        }
        var first = (int)(verticalOffset / RowHeight);
        var last = Math.Min(visible.Length, (int)Math.Ceiling((bottom - RulerHeight) / RowHeight));
        var labelWidth = LabelWidth;
        var valueWidth = Math.Min(65, labelWidth / 3);
        context.PushClip(new RectangleGeometry(new Rect(0, top, ActualWidth, bottom - top)));
        for (var row = first; row < last; row++)
        {
            DrawText(context, ValueAt(visible[row], cursor ?? 0), labelWidth - valueWidth,
                RulerHeight + row * RowHeight + 4, Brushes.Gold, valueWidth - 5);
        }
        context.Pop();
        if (cursor is long selected && selected >= StartTick && selected <= StartTick + SpanTicks)
        {
            context.DrawLine(CursorPen, new(X(selected), verticalOffset + RulerHeight - 5), new(X(selected), bottom));
        }
    }

    private void DrawSignal(DrawingContext context, HdlWaveSignal signal, double y)
    {
        var changes = signal.Changes;
        var end = StartTick + SpanTicks;
        var index = LastChangeAt(changes, StartTick);
        var left = LabelWidth;
        var width = PlotWidth;
        var scale = SpanTicks == 0 ? 0 : width / SpanTicks;
        var levels = new WaveStroke(LevelPen);
        var unknown = new WaveStroke(UnknownPen);
        var impedance = new WaveStroke(ImpedancePen);
        // -1 表示第一次赋值前的未知态。
        while (index < changes.Length)
        {
            var tick = index < 0 ? StartTick : changes[index].Tick;
            if (tick > end)
            {
                break;
            }
            var next = index + 1;
            var rightTick = next < changes.Length ? Math.Min(end, changes[next].Tick) : end;
            var x = left + (Math.Max(StartTick, tick) - StartTick) * scale;
            var right = left + (rightTick - StartTick) * scale;
            if (next < changes.Length && changes[next].Tick <= end && Math.Floor(right) == Math.Floor(x))
            {
                // 跳过同像素内的中间事件，最后一个值仍需画到下一次跳变处。
                var boundary = decimal.Ceiling((decimal)Math.Clamp((Math.Floor(x) + 1 - left) / width, 0, 1) * SpanTicks);
                var last = LastChangeAt(changes, StartTick + (long)boundary - 1);
                impedance.Line(new(x, y + 3), new(x, y + 23));
                index = Math.Max(next, last);
                continue;
            }
            var value = index < 0 ? "x" : changes[index].Value;
            var stroke = value.Contains('x') ? unknown : value.Contains('z') ? impedance : levels;
            if (signal.Width == 1 && stroke == levels)
            {
                var level = value == "1" ? y + 3 : y + 23;
                if (index > 0 && tick >= StartTick)
                {
                    stroke.Line(new(x, value == "1" ? y + 23 : y + 3), new(x, level));
                }
                stroke.Line(new(x, level), new(right, level));
            }
            else
            {
                stroke.Line(new(x, y + 3), new(right, y + 3));
                stroke.Line(new(x, y + 23), new(right, y + 23));
                if (tick >= StartTick)
                {
                    stroke.Line(new(x, y + 3), new(x + Math.Min(4, right - x), y + 23));
                }
                if (right - x > 22)
                {
                    DrawText(context, value, x + 5, y + 4, stroke.Pen.Brush, right - x - 7);
                }
            }
            index++;
        }
        levels.Draw(context);
        unknown.Draw(context);
        impedance.Draw(context);
    }

    // 一行按状态最多提交三条几何路径，避免密集跳变为每条线段创建独立的 WPF 绘图对象。
    private sealed class WaveStroke(Pen pen)
    {
        public Pen Pen => pen;
        private readonly StreamGeometry geometry = new();
        private StreamGeometryContext? path;
        private Point previous;

        public void Line(Point from, Point to)
        {
            if (path is null || previous != from)
            {
                path ??= geometry.Open();
                path.BeginFigure(from, false, false);
            }
            path.LineTo(to, true, false);
            previous = to;
        }

        public void Draw(DrawingContext context)
        {
            if (path is null)
            {
                return;
            }
            path.Close();
            geometry.Freeze();
            context.DrawGeometry(null, pen, geometry);
        }
    }

    private FormattedText MakeText(string text, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, WaveFont,
            textSize, brush, pixelsPerDip);

    private void DrawText(DrawingContext context, string text, double x, double y, Brush brush, double width)
    {
        if (width < 1)
        {
            return;
        }
        var key = (text, brush, Math.Floor(width));
        if (!labelDrawings.TryGetValue(key, out var drawing))
        {
            if (labelDrawings.Count >= 1024)
            {
                labelDrawings.Clear();
            }
            var formatted = MakeText(text, brush);
            formatted.MaxTextWidth = key.Item3;
            formatted.MaxLineCount = 1;
            formatted.Trimming = TextTrimming.CharacterEllipsis;
            drawing = new DrawingGroup();
            using (var label = drawing.Open())
            {
                label.DrawText(formatted, new Point());
            }
            drawing.Freeze();
            labelDrawings.Add(key, drawing);
        }
        context.PushTransform(new TranslateTransform(x, y));
        context.DrawDrawing(drawing);
        context.Pop();
    }
}
