namespace StudioX.Desktop;

using System.Windows.Input;
using StudioX.Application;
using StudioX.Application.CodeIntelligence;

public partial class MainWindow
{
    private sealed record PythonReferenceRow(CodeLocation Location)
    {
        public string File => Location.DisplayPath;
        public int Line => Location.Range.Start.Line + 1;
        public int Column => Location.Range.Start.Character + 1;
    }

    private Task<SourceDocument> ReadCodeLocationAsync(CodeLocation location, CancellationToken token) =>
        PythonAssistanceService.Supports(location.DocumentPath)
            ? services.PythonNavigation.ReadAsync(RequireProject(), location.DocumentPath, currentProjectManifest?.MicroPython, token)
            : services.Intelligence.ReadNavigationDocumentAsync(location, token);

    private void QueuePythonReferences(int? at = null)
    {
        CloseCodeAssistance();
        var offset = at ?? SourceEditor.CaretOffset;
        if (!CanNavigateCode || !NavigationReady || !IsSymbolContext(offset))
        {
            return;
        }
        var cancellation = new CancellationTokenSource();
        navigationCancellation = cancellation;
        var document = SourceEditor.Document;
        var text = document.Text;
        var path = activeDocument!.RelativePath;
        var snapshots = CaptureCodeDocuments();
        var root = RequireProject();
        var profile = currentProjectManifest?.MicroPython;
        var python = IsPythonDocument;
        navigationTask = FindAsync();
        async Task FindAsync()
        {
            try
            {
                var locations = python
                    ? await services.PythonNavigation.FindAsync(root, path, text, offset, true, snapshots, profile, cancellation.Token)
                    : await services.Intelligence.ReferencesAsync(path, text, offset, snapshots, cancellation.Token);
                if (cancellation.IsCancellationRequested || SourceEditor.Document != document || document.Text != text || projectDirectory != root)
                {
                    return;
                }
                PythonReferences.ItemsSource = locations.Select(item => new PythonReferenceRow(item)).ToArray();
                PythonReferencesTab.Header = CreateTabHeader(PythonReferencesTab, new System.Windows.Controls.TextBlock { Text = python ? "Python 引用" : "C/C++ 引用" });
                PythonReferencesStatus.Text = $"找到 {locations.Count} 处（{(python ? "含定义和导入" : "含声明和定义")}）· 双击定位 · 结果基于查找时的编辑快照";
                ShowDocument(PythonReferencesTab);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Status.Text = "查找引用：" + ex.Message;
                Log(ex.ToString());
            }
            finally
            {
                if (navigationCancellation == cancellation)
                {
                    navigationCancellation = null;
                }
                cancellation.Dispose();
            }
        }
    }

    private async void PythonReference_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PythonReferences.SelectedItem is PythonReferenceRow row)
        {
            await RunAsync(token => JumpToCodeAsync(row.Location, token));
        }
    }
}
