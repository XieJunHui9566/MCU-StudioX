namespace StudioX.Desktop;

using System.Windows;
using StudioX.Engine.Debugging;

public partial class MainWindow
{
    private int rtosRequestId;
    private Task debugRtosTask = Task.CompletedTask;
    private CancellationTokenSource? rtosReadCancellation;
    private async Task ReadDebugFreeRtosAsync()
    {
        CancelFreeRtosRead();
        var request = ++rtosRequestId; var snapshot = services.Debugger.Snapshot; var project = services.Debugger.ProjectDirectory;
        bool Current() => !closed && request == rtosRequestId && services.Debugger.State == DebugState.Stopped &&
            ReferenceEquals(snapshot, services.Debugger.Snapshot) && project == services.Debugger.ProjectDirectory;
        if (!Current()) return;
        using var cancellation = new CancellationTokenSource(); rtosReadCancellation = cancellation;
        DebugTools.FreeRtosView.SetLoading();
        try
        {
            var result = await services.Debugger.ReadFreeRtosAsync(DebugTools.FreeRtosView.ObjectSymbols, cancellation.Token);
            if (Current()) DebugTools.FreeRtosView.SetSnapshot(result, services.Debugger.IsHardware);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (Current()) DebugTools.FreeRtosView.SetCanceled();
        }
        catch (Exception ex)
        {
            DebugTools.AppendOutput(ex.ToString());
            if (Current()) DebugTools.FreeRtosView.SetError(ex.Message);
        }
        finally { if (ReferenceEquals(rtosReadCancellation, cancellation)) rtosReadCancellation = null; }
    }
    private void CancelFreeRtosRead()
    {
        if (rtosReadCancellation is null) return;
        DebugTools.FreeRtosView.SetCancelPending(); rtosReadCancellation.Cancel();
    }
    private void ShowFreeRtos_Click(object sender, RoutedEventArgs e)
    {
        ShowDebugLayout(); savedBottomHeight = Math.Clamp(ActualHeight * .43, 280, 410); ShowBottom(2); DebugTools.ShowFreeRtos();
    }
}
