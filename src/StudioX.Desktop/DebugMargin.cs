namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using StudioX.Engine.Debugging;

/// <summary>断点独立于字符排版，缩放、滚动及折行时使用 AvalonEdit 的可见行坐标。</summary>
public sealed class DebugMargin : AbstractMargin, IBackgroundRenderer
{
    public IReadOnlyList<SourceBreakpoint> Breakpoints { get; set; } = [];
    public int? ExecutionLine { get; set; }
    public int? SelectedLine { get; set; }
    public event Action<int>? ToggleRequested;
    public event Action<int>? SettingsRequested;
    public event Action<int>? RunToCursorRequested;
    public bool CanEdit { get; set; }
    public bool CanRunToCursor { get; set; }
    public KnownLayer Layer => KnownLayer.Background;
    protected override Size MeasureOverride(Size availableSize) => new(22, 0);
    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (oldTextView is not null) { oldTextView.VisualLinesChanged -= LinesChanged; oldTextView.BackgroundRenderers.Remove(this); }
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView is not null) { newTextView.VisualLinesChanged += LinesChanged; newTextView.BackgroundRenderers.Add(this); }
    }
    private void LinesChanged(object? sender, EventArgs e) => InvalidateVisual();
    public void Refresh() { InvalidateVisual(); TextView?.InvalidateLayer(KnownLayer.Background); }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (TextView is not { VisualLinesValid: true } view) return;
        foreach (var visual in view.VisualLines)
        {
            var line = visual.FirstDocumentLine.LineNumber;
            var y = visual.VisualTop - view.VerticalOffset + visual.Height / 2;
            var bp = Breakpoints.FirstOrDefault(b => b.Line >= line && b.Line <= visual.LastDocumentLine.LineNumber);
            if (bp is not null)
            {
                var color = new SolidColorBrush(bp.LogMessage is not null ? Color.FromRgb(65, 164, 244) : bp.Temporary || bp.SessionOnly ? Color.FromRgb(239, 170, 66) : Color.FromRgb(242, 89, 100));
                var fill = bp.Enabled && (bp.Verified || bp.Message is null) ? color : Brushes.Transparent;
                var pen = new Pen(bp.Enabled ? color : Brushes.Gray, 1.6);
                if (bp.LogMessage is not null) dc.DrawGeometry(fill, pen, Geometry.Parse(FormattableString.Invariant($"M10,{y - 6} L16,{y} 10,{y + 6} 4,{y} Z")));
                else if (bp.Temporary || bp.SessionOnly) dc.DrawRoundedRectangle(fill, pen, new Rect(5, y - 5, 10, 10), 1, 1);
                else dc.DrawEllipse(fill, pen, new Point(10, y), 5.5, 5.5);
                if (bp.Condition.Length > 0 || bp.IgnoreCount > 0) dc.DrawEllipse(Brushes.White, null, new Point(10, y), 1.6, 1.6);
            }
            if (ExecutionLine == line || SelectedLine == line)
            {
                var geometry = Geometry.Parse(FormattableString.Invariant($"M2,{y - 5} L11,{y - 5} 11,{y - 8} 20,{y} 11,{y + 8} 11,{y + 5} 2,{y + 5} Z"));
                dc.DrawGeometry(ExecutionLine == line ? Brushes.Gold : Brushes.DeepSkyBlue, new Pen(new SolidColorBrush(Color.FromRgb(56, 46, 20)), 0.6), geometry);
            }
        }
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (TextView is not { VisualLinesValid: true } view) return;
        var visual = view.GetVisualLineFromVisualTop(e.GetPosition(this).Y + view.VerticalOffset);
        if (visual is not null) { ToggleRequested?.Invoke(visual.FirstDocumentLine.LineNumber); e.Handled = true; }
    }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (TextView is not { VisualLinesValid: true } view) return;
        var visual = view.GetVisualLineFromVisualTop(e.GetPosition(this).Y + view.VerticalOffset);
        if (visual is null) return;
        var line = visual.FirstDocumentLine.LineNumber;
        var menu = CreateBreakpointMenu(line);
        menu.IsOpen = true; e.Handled = true;
    }
    internal ContextMenu CreateBreakpointMenu(int line)
    {
        var menu = new ContextMenu();
        // 完整复用编辑区菜单模板，避免系统默认图标栏覆盖深色背景和文字。
        menu.SetResourceReference(StyleProperty, "CodeContextMenu");
        var settings = new MenuItem { Header = "条件 / 日志 / 临时断点…", InputGestureText = "Shift+F9", IsEnabled = CanEdit };
        settings.Click += (_, _) => SettingsRequested?.Invoke(line); menu.Items.Add(settings);
        var toggle = new MenuItem { Header = "设置 / 删除断点", InputGestureText = "F9", IsEnabled = CanEdit };
        toggle.Click += (_, _) => ToggleRequested?.Invoke(line); menu.Items.Add(toggle);
        menu.Items.Add(new Separator());
        var run = new MenuItem { Header = "运行到光标", InputGestureText = "Ctrl+F10", IsEnabled = CanRunToCursor };
        run.Click += (_, _) => RunToCursorRequested?.Invoke(line); menu.Items.Add(run);
        menu.PlacementTarget = this;
        return menu;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (TextView is not { VisualLinesValid: true } view) return;
        var line = view.GetVisualLineFromVisualTop(e.GetPosition(this).Y + view.VerticalOffset)?.FirstDocumentLine.LineNumber;
        var point = Breakpoints.FirstOrDefault(b => b.Line == line);
        ToolTip = point is null ? "单击设置断点；右键设置条件、日志或临时断点" : $"{point.Kind}断点 · 第 {point.Line} 行\n{point.Rule}\n已到达 {point.HitCount} 次，剩余跳过 {point.IgnoreRemaining} 次\n{point.Message}";
    }
    public void Draw(TextView textView, DrawingContext dc)
    {
        if (!textView.VisualLinesValid || ExecutionLine is not { } number || textView.Document is null || number > textView.Document.LineCount) return;
        var line = textView.Document.GetLineByNumber(number);
        foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, line))
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(35, 238, 190, 70)), null, new Rect(0, rect.Top, textView.ActualWidth, rect.Height));
    }
}
