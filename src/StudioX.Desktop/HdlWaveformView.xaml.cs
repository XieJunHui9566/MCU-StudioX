namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StudioX.Engine.Hdl;

/// <summary>将 VCD 时间窗口映射到可用宽度，横向滚动只改变窗口起点。</summary>
public partial class HdlWaveformView : UserControl
{
    private sealed record TimeScale(long Span, string Label);
    private HdlWaveform? waveform;
    private decimal? wheelAnchor;
    private int wheelDelta;
    private bool updating;

    public HdlWaveformView()
    {
        InitializeComponent();
        Timebase.DisplayMemberPath = nameof(TimeScale.Label);
        Surface.CursorChanged += text => CursorText.Text = text;
    }

    public void SetWaveform(HdlWaveform? value)
    {
        waveform = value;
        wheelDelta = 0;
        Surface.SetWaveform(value, SignalFilter.Text);
        SignalScroll.ScrollToTop();
        StaleText.Visibility = Visibility.Collapsed;
        CursorText.Text = "尚未选择时间点";
        var scales = new List<TimeScale>();
        if (value is not null)
        {
            // 1、2、5 档均对齐原始 VCD tick，全程档保留最后的不完整一格。
            for (decimal decade = 1; decade * 10 < value.EndTick; decade *= 10)
            {
                foreach (var step in new[] { 1, 2, 5 })
                {
                    var division = decade * step;
                    if (division * 10 < value.EndTick)
                    {
                        scales.Add(new((long)(division * 10), HdlWaveformSurface.FormatTime(division * value.NanosecondsPerTick) + "/格"));
                    }
                }
            }
            scales.Add(new(value.EndTick, "全程"));
        }
        Timebase.ItemsSource = scales;
        Timebase.SelectedIndex = scales.Count - 1;
    }

    public void MarkStale() => StaleText.Visibility = Visibility.Visible;

    private void TimebaseChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Timebase.SelectedItem is not TimeScale scale || waveform is null)
        {
            return;
        }
        var anchor = wheelAnchor ?? Surface.CursorFraction;
        var start = Surface.StartTick + decimal.Round((Surface.SpanTicks - scale.Span) * anchor);
        var maximum = waveform.EndTick - scale.Span;
        Surface.SetTimeWindow((long)Math.Clamp(start, 0, maximum), scale.Span);
        updating = true;
        TimeScroll.Maximum = maximum;
        TimeScroll.LargeChange = Math.Max(1, scale.Span);
        TimeScroll.SmallChange = Math.Max(1, scale.Span / 10);
        TimeScroll.Value = Surface.StartTick;
        updating = false;
        TimeScroll.IsEnabled = FitButton.IsEnabled = ZoomOutButton.IsEnabled = maximum > 0;
        FullRangeHint.Visibility = maximum == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePanButtons();
        ZoomInButton.IsEnabled = Timebase.SelectedIndex > 0;
    }

    private void TimeScrolled(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!updating && waveform is not null)
        {
            var maximum = waveform.EndTick - Surface.SpanTicks;
            var start = e.NewValue >= maximum ? maximum : (long)e.NewValue;
            Surface.SetTimeWindow(start, Surface.SpanTicks);
            UpdatePanButtons();
        }
    }

    private void UpdatePanButtons()
    {
        EarlierButton.IsEnabled = TimeScroll.Value > 0;
        LaterButton.IsEnabled = TimeScroll.Value < TimeScroll.Maximum;
    }

    private void Earlier_Click(object sender, RoutedEventArgs e) => TimeScroll.Value -= TimeScroll.SmallChange;
    private void Later_Click(object sender, RoutedEventArgs e) => TimeScroll.Value += TimeScroll.SmallChange;

    private void SignalsScrolled(object sender, ScrollChangedEventArgs e) =>
        Surface.SetVerticalViewport(e.VerticalOffset, e.ViewportHeight);

    private void FilterChanged(object sender, TextChangedEventArgs e)
    {
        Surface?.Filter(SignalFilter.Text);
        SignalScroll?.ScrollToTop();
    }

    internal bool HandleTimeWheel(DependencyObject? hit, int delta, bool control)
    {
        if (!control || hit != Surface || waveform is null)
        {
            return false;
        }
        wheelDelta += delta;
        var steps = wheelDelta / Mouse.MouseWheelDeltaForOneLine;
        wheelDelta %= Mouse.MouseWheelDeltaForOneLine;
        wheelAnchor = Surface.FractionAt(Mouse.GetPosition(Surface).X);
        Timebase.SelectedIndex = Math.Clamp(Timebase.SelectedIndex - steps, 0, Timebase.Items.Count - 1);
        wheelAnchor = null;
        return true;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Timebase.SelectedIndex--;
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Timebase.SelectedIndex++;
    private void Fit_Click(object sender, RoutedEventArgs e) => Timebase.SelectedIndex = Timebase.Items.Count - 1;
}
