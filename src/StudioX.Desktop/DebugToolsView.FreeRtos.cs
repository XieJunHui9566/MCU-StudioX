namespace StudioX.Desktop;

using StudioX.Engine.Debugging;

public partial class DebugToolsView
{
    private DebugSnapshot? rtosDebugSnapshot;
    public RtosDebugView FreeRtosView => RtosView;
    public void ShowFreeRtos() => DetailTabs.SelectedItem = RtosTab;
    private void RefreshFreeRtos(DebugSnapshot snapshot, DebugState state)
    {
        var changed = !ReferenceEquals(rtosDebugSnapshot, snapshot);
        rtosDebugSnapshot = snapshot; RtosView.RefreshState(state, changed);
        if (state == DebugState.Stopped && changed && RtosTab.IsSelected && RtosView.RefreshOnPause) RequestFreeRtos();
    }
    private void RequestFreeRtos()
    {
        if (RtosView.CanRead) FreeRtosRequested?.Invoke();
    }
    public event Action? FreeRtosRequested;
}
