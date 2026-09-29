namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.CodeIntelligence;

public partial class MainWindow
{
    private readonly DispatcherTimer diagnosticDebounce = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer diagnosticPoll = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private CancellationTokenSource? liveDiagnosticCancellation;
    private Task liveDiagnosticTask = Task.CompletedTask;
    private EditorProblemRow[] problemRows = [];

    private void InitializeLiveDiagnostics()
    {
        diagnosticDebounce.Tick += (_, _) =>
        {
            diagnosticDebounce.Stop();
            if (projectDirectory is null || !services.Intelligence.IsReady || activeDocument is null || !CodeIntelligenceService.Supports(activeDocument.RelativePath) || closing)
            {
                return;
            }
            liveDiagnosticCancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            liveDiagnosticCancellation = cancellation;
            var path = activeDocument.RelativePath;
            var text = SourceEditor.Text;
            var documents = CaptureCodeDocuments();
            liveDiagnosticTask = SynchronizeAsync();
            async Task SynchronizeAsync()
            {
                try
                {
                    await services.Intelligence.SynchronizeDiagnosticsAsync(path, text, documents, cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception ex) { Log("实时诊断：" + ex); }
                finally
                {
                    if (liveDiagnosticCancellation == cancellation)
                    {
                        liveDiagnosticCancellation = null;
                    }
                    cancellation.Dispose();
                }
            }
        };
        diagnosticPoll.Tick += (_, _) => { if (!closing) { RefreshDiagnosticMarkers(); } };
        diagnosticPoll.Start();
    }
    private void QueueLiveDiagnostics()
    {
        diagnosticDebounce.Stop();
        liveDiagnosticCancellation?.Cancel();
        if (!closing && !changingEditor)
        {
            diagnosticDebounce.Start();
        }
    }
    private EditorProblemRow[] CurrentProblems()
    {
        var rows = new List<EditorProblemRow>();
        var liveFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in services.Intelligence.GetDiagnostics())
        {
            if (batch.Project != projectDirectory || FindEditor(batch.Path)?.Buffer.Text != batch.Text)
            {
                continue;
            }
            liveFiles.Add(batch.Path);
            foreach (var item in batch.Items.Where(d => d.Severity is 1 or 2))
            {
                var start = CodePositions.ToOffset(batch.Text, item.Range.Start);
                var end = CodePositions.ToOffset(batch.Text, item.Range.End);
                rows.Add(new(new(batch.Path, item.Range.Start.Line + 1, item.Range.Start.Character + 1, item.Severity == 2, item.Message),
                    "实时 · " + item.Source, batch.Text, start, Math.Max(1, end - start)));
            }
        }
        foreach (var (path, entry) in buildDiagnostics)
        {
            if (liveFiles.Contains(path) || FindEditor(path) is { } editor && editor.Buffer.Text != entry.Text)
            {
                continue;
            }
            rows.AddRange(entry.Items.Select(d => new EditorProblemRow(d, "构建", entry.Text)));
        }
        return rows.OrderBy(r => r.Diagnostic.IsWarning).ThenBy(r => r.File, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Line).ThenBy(r => r.Column).Take(5000).ToArray();
    }
    private async void Problem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProblemsGrid.SelectedItem is not EditorProblemRow row)
        {
            return;
        }
        await RunAsync(async token =>
        {
            await OpenSourceAsync(row.File, token);
            if (SourceEditor.Text != row.Snapshot)
            {
                Status.Text = "诊断对应的文本已变化，正在重新分析。";
                QueueLiveDiagnostics();
                return;
            }
            var marker = row.Offset is { } offset ? new EditorDiagnostic(row.Diagnostic, offset, row.Length) : EditorDiagnosticRenderer.Locate(SourceEditor.Document, row.Diagnostic);
            if (marker is not null)
            {
                SourceEditor.CaretOffset = Math.Clamp(marker.Offset, 0, SourceEditor.Document.TextLength);
                SourceEditor.ScrollToLine(row.Line);
            }
        });
    }
    private void ShowProblems_Click(object sender, RoutedEventArgs e) => ShowBottom(4);
    private async Task StopLiveDiagnosticsAsync()
    {
        diagnosticDebounce.Stop();
        liveDiagnosticCancellation?.Cancel();
        await liveDiagnosticTask;
    }
}
