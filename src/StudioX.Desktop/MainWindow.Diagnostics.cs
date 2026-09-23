namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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
        diagnosticRevision++; buildDiagnostics.Clear(); diagnosticRenderer?.Set([]); HideSymbolHover();
    }
    private async Task PublishBuildDiagnosticsAsync(string directory, string log, long revision, CancellationToken token)
    {
        var parsed = await Task.Run(() => BuildDiagnostics.Parse(directory, log), token);
        foreach (var group in parsed.GroupBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            if (revision != diagnosticRevision || !string.Equals(projectDirectory, directory, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                var source = await services.Files.ReadAsync(directory, group.Key, token);
                if (revision != diagnosticRevision) return;
                // 未保存的缓冲区可能在构建期间改变；绝不把磁盘错误套到其他文本上。
                if (FindEditor(group.Key) is { } session && session.Buffer.Text != source.Text) continue;
                buildDiagnostics[group.Key] = (source.Text, group.ToArray());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            { Log($"无法标记 {group.Key}：{ex.Message}（原始诊断保留在构建输出中）"); }
        }
        RefreshDiagnosticMarkers();
    }
    private void RefreshDiagnosticMarkers()
    {
        diagnosticRenderer?.Set(activeEditor is { } editor && buildDiagnostics.TryGetValue(editor.Source.RelativePath, out var entry) && entry.Text == editor.Buffer.Text
            ? entry.Items.Select(item => EditorDiagnosticRenderer.Locate(editor.Buffer, item)).OfType<EditorDiagnostic>().ToArray() : []);
    }
    private bool TryShowDiagnosticHover(Point point)
    {
        if (activeEditor is null || WorkspaceTabs.SelectedItem != EditorTab || diagnosticRenderer is null) return false;
        var view = SourceEditor.TextArea.TextView;
        var local = SourceEditor.TranslatePoint(point, view);
        if (local.X < 0 || local.Y < 0 || local.X >= view.ActualWidth || local.Y >= view.ActualHeight) return false;
        var position = view.GetPositionFloor(local + view.ScrollOffset);
        if (position is null) return false;
        var line = SourceEditor.Document.GetLineByNumber(position.Value.Line);
        if (position.Value.Column > line.Length + 1) return false;
        var offset = SourceEditor.Document.GetOffset(position.Value.Location);
        var matches = diagnosticRenderer.Markers.Where(m => offset >= m.Offset && offset <= Math.Max(m.Offset, m.EndOffset - 1)).ToArray();
        if (matches.Length == 0) return false;
        HideSymbolHover();
        var body = new StackPanel { MaxWidth = 650, Margin = new Thickness(9, 6, 9, 6) };
        foreach (var marker in matches)
        {
            var diagnostic = marker.Diagnostic;
            body.Children.Add(new TextBlock { Text = diagnostic.IsWarning ? "编译警告" : "编译错误", Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(diagnostic.IsWarning ? "#E8B85B" : "#FF6475")), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 5) });
            body.Children.Add(new TextBlock { Text = diagnostic.Message, TextWrapping = TextWrapping.Wrap });
            var location = new TextBlock { Text = $"{diagnostic.RelativePath}:{diagnostic.Line}" + (diagnostic.Column > 0 ? $":{diagnostic.Column}" : ""), Margin = new Thickness(0, 5, 0, 3), FontSize = 11 };
            location.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); body.Children.Add(location);
        }
        symbolToolTip = new ToolTip { Content = new ScrollViewer { Content = body, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, PlacementTarget = SourceEditor, Placement = PlacementMode.Relative, HorizontalOffset = point.X + 12, VerticalOffset = point.Y + 24, IsHitTestVisible = false, StaysOpen = true };
        symbolToolTip.SetResourceReference(Control.BackgroundProperty, "Surface"); symbolToolTip.SetResourceReference(Control.ForegroundProperty, "Text"); symbolToolTip.SetResourceReference(Control.BorderBrushProperty, "Border");
        symbolToolTip.IsOpen = true; return true;
    }
}
