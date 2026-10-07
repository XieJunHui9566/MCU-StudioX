namespace StudioX.Desktop;

using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using StudioX.Application;

public partial class MainWindow
{
    public async Task RenderInactiveCodePreviewAsync(string directory, string fixture)
    {
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
        }
        async Task Layout()
        {
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            UpdateLayout();
        }
        double Opacity(TextEditor editor, string marker)
        {
            var offset = editor.Text.LastIndexOf(marker, StringComparison.Ordinal);
            var line = editor.TextArea.TextView.VisualLines.Single(line => line.FirstDocumentLine.Offset <= offset && line.LastDocumentLine.EndOffset > offset);
            var element = line.Elements.Single(element => line.FirstDocumentLine.Offset + element.RelativeTextOffset <= offset &&
                line.FirstDocumentLine.Offset + element.RelativeTextOffset + element.DocumentLength > offset);
            return element.TextRunProperties.ForegroundBrush.Opacity;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        var original = await File.ReadAllTextAsync(Path.Combine(fixture, "src/inactive.c"), token);
        try
        {
            await OpenProjectAsync(fixture, token);
            await OpenSourceAsync("src/inactive.c", token);
            var main = activeEditor!;
            await OpenSourceAsync("src/other.c", token);
            var other = activeEditor!;
            MoveEditorGroup(other, 1);
            ShowDocument(main.Tab);
            await services.Intelligence.SynchronizeDiagnosticsAsync(main.Source.RelativePath, main.Buffer.Text, CaptureCodeDocuments(), token);
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
            {
                ApplyTheme(theme);
                RefreshSplitMirror();
                await bracketColors!.RefreshAsync();
                RefreshDiagnosticMarkers();
                SourceEditor.ScrollToHome();
                mirrorEditor.ScrollToHome();
                await Layout();
                Check(inactiveCode!.Spans.Count > 0 && mirrorInactiveCode!.Spans.Count > 0, theme.Id + ": both editor groups receive compiler inactive regions");
                Check(Opacity(SourceEditor, "Board_Delay(milliseconds)") < .6 && Opacity(SourceEditor, "vTaskDelay(pdMS_TO_TICKS") > .9,
                    theme.Id + ": actual RTOS source glyphs dim the disabled bare-metal branch while active syntax remains bright");
                Check(Opacity(mirrorEditor, "vTaskDelay(pdMS_TO_TICKS") < .6 && Opacity(mirrorEditor, "Board_Delay(milliseconds)") > .9,
                    theme.Id + ": identical source in a second target uses its own opposite branch state");
                var bracket = original.IndexOf("Board_Delay(milliseconds)", StringComparison.Ordinal) + "Board_Delay".Length;
                var line = SourceEditor.TextArea.TextView.VisualLines.Single(line => line.FirstDocumentLine.Offset <= bracket && line.LastDocumentLine.EndOffset > bracket);
                var glyph = line.Elements.Single(element => line.FirstDocumentLine.Offset + element.RelativeTextOffset <= bracket &&
                    line.FirstDocumentLine.Offset + element.RelativeTextOffset + element.DocumentLength > bracket);
                Check(glyph.TextRunProperties.ForegroundBrush.Opacity < .6, theme.Id + ": bracket coloring cannot repaint inactive code at full brightness");
                Render(this, Path.Combine(directory, "inactive-code-" + theme.Id + ".png"));
            }
            main.Buffer.Insert(0, "// unsaved revision\r\n");
            Check(inactiveCode!.Spans.Count == 0 && mirrorInactiveCode!.Spans.Count == 0, "editing immediately clears stale ranges in both editor groups before debounce");
            await services.Intelligence.SynchronizeDiagnosticsAsync(main.Source.RelativePath, main.Buffer.Text, CaptureCodeDocuments(), token);
            RefreshDiagnosticMarkers();
            Check(inactiveCode.Spans.Count > 0, "fresh analysis reapplies ranges at the new unsaved text coordinates");
            ShowDocument(other.Tab);
            await Layout();
            Check(Opacity(SourceEditor, "vTaskDelay(pdMS_TO_TICKS") < .6, "switching active source cannot reuse the previous file's branch colors");
            Check(await File.ReadAllTextAsync(Path.Combine(fixture, "src/inactive.c"), token) == original, "inactive highlighting never changes source text or saves preview edits");
            await CloseProjectAsync(token, _ => System.Windows.MessageBoxResult.No);
            Check(inactiveCode.Spans.Count == 0 && mirrorInactiveCode!.Spans.Count == 0 && services.Intelligence.GetInactiveRegions().Count == 0,
                "closing the project removes all inactive colors and language analysis state");
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS " + checks.Count + "; real WPF and clangd; no firmware build or hardware\n" + string.Join('\n', checks), token);
        }
        finally { ClearEditorDocuments(); await services.Intelligence.StopAsync(); }
    }
}
