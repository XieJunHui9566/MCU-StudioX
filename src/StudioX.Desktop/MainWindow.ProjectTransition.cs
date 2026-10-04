namespace StudioX.Desktop;

public partial class MainWindow
{
    private ProjectTransitionCoordinator CreateProjectTransitionCoordinator() => new(
        stopAgentAsync: async () =>
        {
            await MicroPythonPanel.StopAsync();
            MicroPythonPanel.SetProject(null, null);
            PythonReferences.ItemsSource = null;
            PythonReferencesTab.Visibility = System.Windows.Visibility.Collapsed;
            await StopPluginWorkspaceAsync();
            aiCancellation?.Cancel();
            await DisposeAiMcpSessionAsync();
        },
        persistBreakpointsAsync: PersistBreakpointLinesAsync,
        stopPreviewAsync: LvglPreview.StopProjectAsync,
        stopDebuggerAsync: StopProjectDebuggerAsync,
        stopEditorAsync: StopProjectEditorAsync);

    private async Task StopProjectDebuggerAsync(CancellationToken token)
    {
        CancelFreeRtosRead();
        await services.Debugger.OpenProjectAsync(null, token);
        await Task.WhenAll(debugNavigationTask, debugRtosTask);
        debugAnchors.Clear();
        lastDebugSnapshot = null;
        DebugTools.ClearBreakpointLog();
        HideDebugLayout();
    }

    private async Task StopProjectEditorAsync(CancellationToken token)
    {
        await StopLiveDiagnosticsAsync();
        ClearWorkspaceEditing();
        peripheralUndo = null;
        CloseCodeAssistance();
        if (sourceContextMenu is not null)
        {
            sourceContextMenu.IsOpen = false;
        }
        if (explorerMenu is not null)
        {
            explorerMenu.IsOpen = false;
        }
        await assistTask;
        await Task.WhenAll(hoverTask, navigationTask);
        token.ThrowIfCancellationRequested();
        await services.Intelligence.StopAsync();
    }
}
