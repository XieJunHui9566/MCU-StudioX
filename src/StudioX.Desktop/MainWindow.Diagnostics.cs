namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using StudioX.Application;

public partial class MainWindow
{
    private EditorDiagnosticRenderer? diagnosticRenderer;
    private long diagnosticRevision;
    private readonly Dictionary<string, (string Text, BuildDiagnostic[] Items)> buildDiagnostics = new(StringComparer.OrdinalIgnoreCase);

    private void InitializeDiagnostics()
    {
        diagnosticRenderer = new(SourceEditor.TextArea.TextView);
        SourceEditor.TextArea.TextView.BackgroundRenderers.Add(diagnosticRenderer);
    }
    private void ClearBuildDiagnostics()
    {
        diagnosticRevision++;
        buildDiagnostics.Clear();
        RefreshDiagnosticMarkers();
        HideSymbolHover();
    }
    private async Task PublishBuildDiagnosticsAsync(string directory, string log, long revision, CancellationToken token)
    {
        var parsed = await Task.Run(() => BuildDiagnostics.Parse(directory, log), token);
        foreach (var group in parsed.GroupBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            if (revision != diagnosticRevision || !string.Equals(projectDirectory, directory, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            try
            {
                var source = await services.Files.ReadAsync(directory, group.Key, token);
                if (revision != diagnosticRevision)
                {
                    return;
                }
                // 未保存的缓冲区可能在构建期间改变；绝不把磁盘错误套到其他文本上。
                if (FindEditor(group.Key) is { } session && session.Buffer.Text != source.Text)
                {
                    continue;
                }
                buildDiagnostics[group.Key] = (source.Text, group.ToArray());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                Log($"无法标记 {group.Key}：{ex.Message}（原始诊断保留在构建输出中）");
            }
        }
        RefreshDiagnosticMarkers();
    }
    private void RefreshDiagnosticMarkers()
    {
        var current = CurrentProblems();
        if (!problemRows.SequenceEqual(current))
        {
            problemRows = current;
            ProblemsGrid.ItemsSource = current;
        }
        var batches = services.Intelligence.GetDiagnostics();
        var state = projectDirectory is not null && currentProjectManifest?.Kind != StudioX.Engine.ProjectKind.MicroPython
            ? services.Debugger.IsActive || diagnosticsPausedForDebug ? " · 实时分析已暂停"
            : !services.Intelligence.IsReady ? " · 实时分析未就绪"
            : batches.Any(batch => !batch.IsComplete) ? " · 诊断数据不完整"
            : activeEditor is { } currentEditor && StudioX.Application.CodeIntelligence.CodeIntelligenceService.Supports(currentEditor.Source.RelativePath) &&
                !batches.Any(batch => batch.Path.Equals(currentEditor.Source.RelativePath, StringComparison.OrdinalIgnoreCase) && batch.Text == currentEditor.Buffer.Text)
                ? " · 正在分析" : ""
            : "";
        ProblemsTab.Header = $"问题 ({current.Length})" + state;
        diagnosticRenderer?.Set(!services.Debugger.IsActive && !diagnosticsPausedForDebug && activeEditor is { } editor ? current.Where(r => r.File.Equals(editor.Source.RelativePath, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Offset is { } offset ? new EditorDiagnostic(r.Diagnostic, offset, Math.Min(r.Length, Math.Max(0, editor.Buffer.TextLength - offset))) : EditorDiagnosticRenderer.Locate(editor.Buffer, r.Diagnostic))
            .OfType<EditorDiagnostic>().ToArray() : []);
    }
    private bool TryShowDiagnosticHover(Point point)
    {
        if (services.Debugger.IsActive || diagnosticsPausedForDebug || activeEditor is null || !IsActiveSourceTab || diagnosticRenderer is null)
        {
            return false;
        }
        var view = SourceEditor.TextArea.TextView;
        var local = SourceEditor.TranslatePoint(point, view);
        if (local.X < 0 || local.Y < 0 || local.X >= view.ActualWidth || local.Y >= view.ActualHeight)
        {
            return false;
        }
        var position = view.GetPositionFloor(local + view.ScrollOffset);
        if (position is null)
        {
            return false;
        }
        var line = SourceEditor.Document.GetLineByNumber(position.Value.Line);
        if (position.Value.Column > line.Length + 1)
        {
            return false;
        }
        var offset = SourceEditor.Document.GetOffset(position.Value.Location);
        var matches = diagnosticRenderer.Markers.Where(m => offset >= m.Offset && offset <= Math.Max(m.Offset, m.EndOffset - 1)).ToArray();
        if (matches.Length == 0)
        {
            return false;
        }
        HideSymbolHover();
        var body = new StackPanel { MaxWidth = 650, Margin = new Thickness(9, 6, 9, 6) };
        foreach (var marker in matches)
        {
            var diagnostic = marker.Diagnostic;
            var severity = new TextBlock { Text = diagnostic.IsWarning ? "警告 Warning" : "错误 Error", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 5) };
            severity.SetResourceReference(TextBlock.ForegroundProperty, diagnostic.IsWarning ? "DiagnosticWarning" : "DiagnosticError");
            body.Children.Add(severity);
            body.Children.Add(new TextBlock { Text = DiagnosticText.ChineseSummary(diagnostic.Message), TextWrapping = TextWrapping.Wrap });
            var original = new TextBlock { Text = diagnostic.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            original.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            body.Children.Add(original);
            var location = new TextBlock { Text = $"{diagnostic.RelativePath}:{diagnostic.Line}" + (diagnostic.Column > 0 ? $":{diagnostic.Column}" : ""), Margin = new Thickness(0, 5, 0, 3), FontSize = 11 };
            location.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            body.Children.Add(location);
        }
        symbolToolTip = new ToolTip { Content = new ScrollViewer { Content = body, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, PlacementTarget = SourceEditor, Placement = PlacementMode.Relative, HorizontalOffset = point.X + 12, VerticalOffset = point.Y + 24, IsHitTestVisible = false, StaysOpen = true };
        symbolToolTip.SetResourceReference(Control.BackgroundProperty, "Surface");
        symbolToolTip.SetResourceReference(Control.ForegroundProperty, "Text");
        symbolToolTip.SetResourceReference(Control.BorderBrushProperty, "Border");
        symbolToolTip.IsOpen = true;
        return true;
    }
}
