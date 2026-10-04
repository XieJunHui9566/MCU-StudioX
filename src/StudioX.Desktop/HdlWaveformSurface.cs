namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StudioX.Engine.Hdl;

/// <summary>按实际 VCD 时间绘制数字波形，光标显示采样时刻的值。</summary>
public sealed class HdlWaveformSurface : FrameworkElement
{
    private HdlWaveform? waveform;
    private HdlWaveSignal[] visible = [];
    private double zoom = 1;
    private long cursor;
    private bool draggingCursor;
    private int draggedSignalRow;
    private const double LabelWidth = 260;
    private const double RowHeight = 36;
    public event Action<string>? CursorChanged;

    public void SetWaveform(HdlWaveform? value)
    {
        StopCursorDrag();
        waveform = value;
        zoom = 1;
        cursor = 0;
        Filter("");
    }
    public void Filter(string text)
    {
        StopCursorDrag();
        visible = waveform?.Signals.Where(signal => signal.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(128).ToArray() ?? [];
        UpdateSize();
    }
    public void Zoom(double factor)
    {
        zoom = Math.Clamp(zoom * factor, .25, 16);
        UpdateSize();
    }
    private void UpdateSize()
    {
        Width = LabelWidth + 1000 * zoom;
        Height = Math.Max(200, 50 + visible.Length * RowHeight);
        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        var position = e.GetPosition(this);
        if (waveform is null || e.ChangedButton != MouseButton.Left || position.X < LabelWidth)
        {
            return;
        }
        draggedSignalRow = (int)Math.Floor((position.Y - 40) / RowHeight);
        MoveCursor(position, draggedSignalRow);
        // 捕获鼠标后，即使拖出波形区域，松开左键也能结束操作，不留下粘住的光标。
        draggingCursor = CaptureMouse();
        Cursor = Cursors.SizeWE;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        if (draggingCursor)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                StopCursorDrag();
                return;
            }
            MoveCursor(position, draggedSignalRow);
            e.Handled = true;
            return;
        }
        var cursorX = LabelWidth + (waveform?.EndTick > 0 ? (double)cursor / waveform.EndTick * 1000 * zoom : 0);
        if (waveform is not null && Math.Abs(position.X - cursorX) <= 6)
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
        if (!draggingCursor || e.ChangedButton != MouseButton.Left)
        {
            return;
        }
        MoveCursor(e.GetPosition(this), draggedSignalRow);
        StopCursorDrag();
        e.Handled = true;
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
        draggingCursor = false;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        ClearValue(CursorProperty);
    }

    private void MoveCursor(Point position, int row)
    {
        if (waveform is null)
        {
            return;
        }
        cursor = (long)(Math.Clamp((position.X - LabelWidth) / (1000 * zoom), 0, 1) * waveform.EndTick);
        var sample = row >= 0 && row < visible.Length ? visible[row] : null;
        CursorChanged?.Invoke($"光标 {cursor * waveform.NanosecondsPerTick:0.###} ns" + (sample is null
            ? $" · 显示前 {visible.Length} 条匹配信号"
            : $" · {sample.Name} [{sample.Width}] = {sample.Changes.LastOrDefault(change => change.Tick <= cursor)?.Value ?? "x"}"));
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(20, 27, 39)), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (waveform is null)
        {
            Text("运行 testbench 后显示 RTL 波形", 14, 20, Brushes.LightGray);
            return;
        }
        double X(long tick) => LabelWidth + (waveform.EndTick == 0 ? 0 : (double)tick / waveform.EndTick * 1000 * zoom);
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(55, 65, 80)), 1);
        var highLowPen = new Pen(Brushes.LightGreen, 1.4);
        var unknownPen = new Pen(Brushes.OrangeRed, 1.4);
        var highImpedancePen = new Pen(Brushes.Gold, 1.4);
        for (var i = 0; i <= 10; i++)
        {
            var tick = waveform.EndTick / 10 * i;
            Text($"{tick * waveform.NanosecondsPerTick:0.###} ns", X(tick) + 3, 7, Brushes.LightGray);
            context.DrawLine(grid, new(X(tick), 30), new(X(tick), ActualHeight));
        }
        for (var row = 0; row < visible.Length; row++)
        {
            var signal = visible[row];
            var y = 40 + row * RowHeight;
            var value = signal.Changes.LastOrDefault(change => change.Tick <= cursor)?.Value ?? "x";
            Text(signal.Name, 8, y, Brushes.WhiteSmoke, LabelWidth - 72);
            Text(value.Length > 8 ? value[..8] + "…" : value, LabelWidth - 65, y, Brushes.Gold, 60);
            context.DrawLine(grid, new(0, y + 28), new(ActualWidth, y + 28));
            for (var i = 0; i < signal.Changes.Length; i++)
            {
                var change = signal.Changes[i];
                var end = i + 1 < signal.Changes.Length ? signal.Changes[i + 1].Tick : waveform.EndTick;
                var x = X(change.Tick);
                var right = X(end);
                // 同一像素中的密集跳变聚合成活动竖线，避免为百万事件逐个创建绘图对象。
                if (right - x < 1 && i + 1 < signal.Changes.Length)
                {
                    var column = Math.Floor(x);
                    var first = i;
                    while (i + 1 < signal.Changes.Length && Math.Floor(X(signal.Changes[i + 1].Tick)) == column)
                    {
                        i++;
                    }
                    context.DrawLine(highImpedancePen, new(column, y + 3), new(column, y + 23));
                    if (i > first)
                    {
                        i--;
                        continue;
                    }
                }
                var unknown = change.Value.Contains('x');
                var color = unknown ? Brushes.OrangeRed : change.Value.Contains('z') ? Brushes.Gold : Brushes.LightGreen;
                var pen = unknown ? unknownPen : change.Value.Contains('z') ? highImpedancePen : highLowPen;
                if (signal.Width == 1 && !unknown && change.Value != "z")
                {
                    var level = change.Value == "1" ? y + 3 : y + 23;
                    context.DrawLine(pen, new(x, level), new(right, level));
                    if (i > 0)
                    {
                        context.DrawLine(pen, new(x, y + 3), new(x, y + 23));
                    }
                }
                else
                {
                    context.DrawLine(pen, new(x, y + 3), new(right, y + 3));
                    context.DrawLine(pen, new(x, y + 23), new(right, y + 23));
                    context.DrawLine(pen, new(x, y + 3), new(x + Math.Min(4, right - x), y + 23));
                    if (right - x > 22)
                    {
                        Text(change.Value, x + 5, y + 4, color, right - x - 7);
                    }
                }
            }
        }
        context.DrawLine(new Pen(Brushes.DeepSkyBlue, 1), new(X(cursor), 30), new(X(cursor), ActualHeight));

        void Text(string text, double x, double y, Brush brush, double width = 200)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Consolas"), 12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, width),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis
            };
            context.DrawText(formatted, new(x, y));
        }
    }
}
