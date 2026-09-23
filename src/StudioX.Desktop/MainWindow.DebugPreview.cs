namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine.Debugging;

public partial class MainWindow
{
    public Task ShowDebugDemoAsync(string packArchive) => RunAsync(async token =>
    {
        if (!await CloseProjectAsync(token)) return;
        await services.Packs.ImportAsync(packArchive, token); await RefreshPacksAsync(token);
        var root = await DebugSessionService.CreateExampleAsync(services.Packs, services.DataDirectory, Path.Combine(AppContext.BaseDirectory, "device-packs"), token);
        await OpenProjectAsync(root, token);
        await services.Debugger.ToggleBreakpointAsync(F407DebugExample.RelativeFile, F407DebugExample.Call, token);
        await services.Debugger.StartOfflineAsync(token); await NavigateSelectedDebugFrameAsync();
        Status.Text = "F407 离线调试 · F9 断点 / F5 运行 / F10 逐过程 / F11 进入 · 未连接芯片";
    });

    public async Task RenderDebugPreviewAsync(string directory, string packArchive)
    {
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await debugNavigationTask;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        async Task WaitStopped()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (services.Debugger.State != DebugState.Stopped) await Task.Delay(20, timeout.Token);
            await Settle();
        }
        Check(RegistersTab.Visibility == Visibility.Collapsed && DebugTab.Visibility == Visibility.Collapsed, "Debug tabs should be hidden before starting");
        await ShowDebugDemoAsync(packArchive); await Settle();
        Check(services.Debugger.State == DebugState.Stopped, "Demo start: " + Status.Text);
        Check(RegistersTab.Visibility == Visibility.Visible && DebugTab.Visibility == Visibility.Visible, "Debug tabs should appear after starting");
        Check(DebugContinueButton.IsEnabled && !DebugPauseButton.IsEnabled && !BuildButton.IsEnabled && !DownloadButton.IsEnabled, "Stopped controls");
        Check(SourceEditor.IsReadOnly && RegisterGrid.Items.Count == 56 && debugMargin!.ExecutionLine == F407DebugExample.Entry, "Editor lock, registers or execution arrow");
        DebugContinue_Click(this, new RoutedEventArgs()); await pendingOperation; await WaitStopped();
        Check(SourceEditor.TextArea.Caret.Line == F407DebugExample.Call && services.Debugger.Reason.Contains("命中断点", StringComparison.Ordinal), "Run to breakpoint navigation");
        DebugInto_Click(this, new RoutedEventArgs()); await pendingOperation; await WaitStopped();
        Check(DebugOutButton.IsEnabled && services.Debugger.Snapshot.Frames.Length == 2 && SourceEditor.TextArea.Caret.Line == F407DebugExample.Function, "Step into UI");
        DebugOver_Click(this, new RoutedEventArgs()); await pendingOperation; await WaitStopped();
        Check(services.Debugger.Snapshot.Locals.Single(v => v.Name == "doubled").Value == "2", "Locals refresh");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme); UpdateLayout(); await Settle();
            Render(this, Path.Combine(directory, "debug-" + theme.Id + ".png"));
            DebugTools.ShowLocals(); UpdateLayout(); await Settle();
            Render(DebugTools, Path.Combine(directory, "locals-" + theme.Id + ".png"));
        }
        ApplyTheme(ThemeService.Dark); Width = 1100; Height = 720; UpdateLayout(); await Settle();
        Check(BottomPanel.TranslatePoint(new Point(0, BottomPanel.ActualHeight), WindowRoot).Y <= WindowRoot.ActualHeight - 24, "Bottom debug panel clipped at minimum size");
        Check(DebugToolbar.ActualHeight > 30 && SourceEditor.ActualHeight > 120, "Editor or debug toolbar collapsed");
        Render(this, Path.Combine(directory, "debug-minimum.png"));
        Width = 1460; Height = 920;
        await services.Debugger.RefreshAsync(1); await Settle();
        Check(debugMargin!.SelectedLine == F407DebugExample.Call, "Selected stack frame marker");
        DebugOut_Click(this, new RoutedEventArgs()); await pendingOperation; await WaitStopped();
        Check(services.Debugger.Snapshot.Frames.Length == 1 && !DebugOutButton.IsEnabled, "Outermost controls");
        await services.Debugger.ChangeBreakpointAsync(services.Debugger.Breakpoints.Single().Id, false);
        DebugContinue_Click(this, new RoutedEventArgs()); await pendingOperation; await Settle();
        Check(services.Debugger.State == DebugState.Running && Status.Text == services.Debugger.Reason && DebugStateText.Text == Status.Text,
            "Running state must reach both debug toolbar and bottom status");
        DebugPause_Click(this, new RoutedEventArgs()); await pendingOperation; await WaitStopped();
        Check(Status.Text == services.Debugger.Reason, "Pause notification must refresh bottom status");
        DebugStop_Click(this, new RoutedEventArgs()); await pendingOperation; await Settle();
        Check(!SourceEditor.IsReadOnly && BuildButton.IsEnabled && debugMargin.ExecutionLine is null && RegisterGrid.Items.Count == 0, "End debugging restores edit/build and clears snapshots");
        Check(RegistersTab.Visibility == Visibility.Collapsed && DebugTab.Visibility == Visibility.Collapsed && LeftToolTabs.SelectedIndex == 0 && BottomTabs.SelectedIndex == 0, "Debug tabs should hide after stopping");
        var point = services.Debugger.Breakpoints.Single();
        SourceEditor.Document.Insert(0, "// anchor test\n"); await PersistBreakpointLinesAsync(); await Settle();
        Check(services.Debugger.Breakpoints.Single().Line == point.Line && debugMargin.Breakpoints.Single().Line == point.Line + 1, "Unsaved edit should move visible marker without changing persisted position");
        await SaveAllSourcesAsync(RequireProject(), CancellationToken.None); await Settle();
        Check(services.Debugger.Breakpoints.Single().Line == point.Line + 1, "Breakpoint anchor did not follow inserted line");
        SourceEditor.Document.UndoStack.Undo(); await SaveAllSourcesAsync(RequireProject(), CancellationToken.None); await Settle();
        Check(services.Debugger.Breakpoints.Single().Line == point.Line, "Breakpoint anchor did not follow undo");
        await CloseProjectAsync(CancellationToken.None); await Settle();
        Check(services.Debugger.Breakpoints.Count == 0 && DebugToolbar.Visibility == Visibility.Collapsed && RegisterGrid.Items.Count == 0, "Close did not clear debug UI");
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: F407 offline demo; gutter binding; entry/stop navigation; run/step into/over/out; call-stack selection; register/locals UI; running/paused status synchronization; busy/read-only states; restore edit/build after stop; breakpoint anchors follow edits and undo; project cleanup; dark/light/minimum-size renders. No hardware accessed.\n");
    }
}
