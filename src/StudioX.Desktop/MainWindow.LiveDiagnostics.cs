namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using StudioX.Application.CodeIntelligence;

public partial class MainWindow
{
    private readonly DispatcherTimer diagnosticDebounce = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer diagnosticPoll = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private CancellationTokenSource? liveDiagnosticCancellation;
    private Task liveDiagnosticTask = Task.CompletedTask;
    private Task analysisEnvironmentTask = Task.CompletedTask;
    private CancellationTokenSource? analysisEnvironmentCancellation;
    private EditorProblemRow[] problemRows = [];
    private bool diagnosticsPausedForDebug;
    private CodeDiagnosticBatch[] verilogBatches = [];
    private string verilogDiagnosticStatus = "";

    private void UpdateDebugDiagnosticState()
    {
        var paused = services.Debugger.IsActive;
        if (diagnosticsPausedForDebug == paused)
        {
            return;
        }
        diagnosticsPausedForDebug = paused;
        diagnosticDebounce.Stop();
        liveDiagnosticCancellation?.Cancel();
        services.Intelligence.SetDiagnosticsSuspended(paused);
        HideSymbolHover();
        RefreshDiagnosticMarkers();
        if (!paused)
        {
            QueueLiveDiagnostics();
        }
    }

    private void InitializeLiveDiagnostics()
    {
        diagnosticDebounce.Tick += (_, _) =>
        {
            diagnosticDebounce.Stop();
            if (services.Debugger.IsActive || diagnosticsPausedForDebug || projectDirectory is null || activeDocument is null || closing ||
                (!IsVerilogDocument && (!services.Intelligence.IsReady || !CodeIntelligenceService.Supports(activeDocument.RelativePath))))
            {
                return;
            }
            liveDiagnosticCancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            liveDiagnosticCancellation = cancellation;
            var root = projectDirectory;
            var isVerilog = IsVerilogDocument;
            var path = activeDocument.RelativePath;
            var text = SourceEditor.Text;
            var documents = CaptureCodeDocuments();
            liveDiagnosticTask = SynchronizeAsync();
            async Task SynchronizeAsync()
            {
                try
                {
                    if (isVerilog)
                    {
                        var result = await services.Verilog.DiagnoseAsync(root, path, text, documents, cancellation.Token);
                        if (!cancellation.IsCancellationRequested && root == projectDirectory)
                        {
                            verilogBatches = result.Batches;
                            verilogDiagnosticStatus = result.Status;
                            RefreshDiagnosticMarkers();
                        }
                    }
                    else

                    {
                        await services.Intelligence.SynchronizeDiagnosticsAsync(path, text, documents, cancellation.Token);
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    if (isVerilog && !cancellation.IsCancellationRequested)
                    {
                        verilogBatches = [];
                        verilogDiagnosticStatus = "Verilog 分析不可用 · 见分析日志";
                        RefreshDiagnosticMarkers();
                    }
                    await RecordAnalysisFailureAsync("实时诊断", ex);
                }
                finally
                {
                    if (liveDiagnosticCancellation == cancellation)
                    {
                        liveDiagnosticCancellation = null;
                        if (isVerilog && !closing)
                        {
                            RefreshDiagnosticMarkers();
                        }
                    }
                    cancellation.Dispose();
                }
            }
        };
        diagnosticPoll.Tick += (_, _) =>
        {
            if (closing)
            {
                return;
            }
            RefreshDiagnosticMarkers();
            if (projectDirectory is null || currentProjectManifest?.Kind == StudioX.Engine.ProjectKind.MicroPython ||
                projectActionsBusy || services.Debugger.IsActive || !analysisEnvironmentTask.IsCompleted)
            {
                return;
            }
            var root = projectDirectory;
            var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            analysisEnvironmentCancellation = cancellation;
            analysisEnvironmentTask = RefreshAsync();
            async Task RefreshAsync()
            {
                try
                {
                    var refreshed = await services.Intelligence.RefreshEnvironmentAsync(cancellation.Token);
                    if (projectDirectory == root && !closing)
                    {
                        if (refreshed)
                        {
                            QueueLiveDiagnostics();
                            QueueOutlineRefresh(clear: true);
                        }
                        if (refreshed || !services.Intelligence.IsReady)
                        {
                            Status.Text = services.Intelligence.StatusDescription;
                        }
                        await FlushAnalysisLogQuietlyAsync();
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
                catch (Exception error)
                {
                    if (projectDirectory == root && !closing)
                    {
                        RefreshDiagnosticMarkers();
                        Status.Text = services.Intelligence.StatusDescription;
                        await RecordAnalysisFailureAsync("分析环境需要修复，请打开工程健康检查", error);
                    }
                }
                finally
                {
                    if (analysisEnvironmentCancellation == cancellation)
                    {
                        analysisEnvironmentCancellation = null;
                    }
                    cancellation.Dispose();
                }
            }
        };
        diagnosticPoll.Start();
    }
    private void QueueLiveDiagnostics()
    {
        diagnosticDebounce.Stop();
        liveDiagnosticCancellation?.Cancel();
        if (!closing && !changingEditor && !services.Debugger.IsActive && !diagnosticsPausedForDebug)
        {
            diagnosticDebounce.Interval = TimeSpan.FromMilliseconds(IsVerilogDocument ? 400 : 1500);
            verilogBatches = [];
            verilogDiagnosticStatus = "";
            diagnosticDebounce.Start();
            RefreshDiagnosticMarkers();
        }
    }
    private EditorProblemRow[] CurrentProblems()
    {
        var rows = new List<EditorProblemRow>();
        var liveFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 补全也会同步文档并提前收到 clangd 诊断，显示必须同样等待停笔。
        foreach (var batch in services.Debugger.IsActive || diagnosticsPausedForDebug || diagnosticDebounce.IsEnabled ? [] : services.Intelligence.GetDiagnostics().Concat(verilogBatches))
        {
            var editor = FindEditor(batch.Path);
            if (batch.Project != projectDirectory ||
                (editor is null ? !VerilogAssistanceService.Supports(batch.Path) : editor.Buffer.Text != batch.Text))
            {
                continue;
            }
            if (batch.IsComplete)
            {
                liveFiles.Add(batch.Path);
            }
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
            rows.AddRange(entry.Items.Select(d => new EditorProblemRow(d, entry.Origin, entry.Text)));
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
        analysisEnvironmentCancellation?.Cancel();
        await Task.WhenAll(liveDiagnosticTask, analysisEnvironmentTask);
    }
}
