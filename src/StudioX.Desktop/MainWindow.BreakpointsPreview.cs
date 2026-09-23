namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine.Debugging;

public partial class MainWindow
{
    public async Task ShowSpecialBreakpointDemoAsync(string packArchive)
    {
        await ShowDebugDemoAsync(packArchive);
        await RunAsync(async token =>
        {
            if (services.Debugger.State != DebugState.Stopped) return;
            await services.Debugger.ConfigureBreakpointAsync(F407DebugExample.RelativeFile, F407DebugExample.Call, new("app_counter >= 3", IgnoreCount: 1), token);
            await services.Debugger.ConfigureBreakpointAsync(F407DebugExample.RelativeFile, F407DebugExample.Delay, new(LogMessage: "count={app_counter}, output={app_output}"), token);
            await services.Debugger.ConfigureBreakpointAsync(F407DebugExample.RelativeFile, F407DebugExample.Function, new("app_counter >= 4", Temporary: true), token);
            ShowBottom(2); DebugTools.ShowBreakpoints(); RefreshDebugUi();
            Status.Text = "特殊断点示例 · F5 运行：前两轮输出日志，第 3 轮条件暂停 · 右键断点可设置规则";
        });
    }
    public async Task RenderBreakpointsPreviewAsync(string directory, string packArchive)
    {
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await debugNavigationTask; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        async Task WaitStopped()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (services.Debugger.State != DebugState.Stopped) await Task.Delay(20, timeout.Token);
            await Settle();
        }
        await ShowSpecialBreakpointDemoAsync(packArchive); await Settle();
        Check(services.Debugger.Breakpoints.Count == 3 && debugMargin!.Breakpoints.Count == 3, "Special demo binding");
        var edited = new BreakpointOptions("app_counter >= 3 && (app_input & 1) == 1", 1, true, "{{sample}} counter={app_counter}, pc={$pc}");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            var dialog = new BreakpointWindow(F407DebugExample.RelativeFile, F407DebugExample.Call, edited)
            { Owner = this, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -18000 };
            dialog.Show(); dialog.UpdateLayout(); await Settle();
            Check(dialog.ReadOptions() && dialog.Options == edited, "Dialog roundtrip");
            Render(dialog, Path.Combine(directory, "breakpoint-settings-" + theme.Id + ".png"));
            ((TextBox)dialog.FindName("ConditionInput")).Text = "app_counter = 1";
            Check(!dialog.ReadOptions() && ((TextBlock)dialog.FindName("ErrorLabel")).Text.Length > 0, "Dialog rejected assignment without feedback");
            ((TextBox)dialog.FindName("ConditionInput")).Text = "app_counter > 0";
            ((TextBox)dialog.FindName("IgnoreInput")).Text = "-1";
            Check(!dialog.ReadOptions(), "Negative ignore count allowed");
            dialog.Close(); UpdateLayout(); await Settle();
            Render(this, Path.Combine(directory, "special-breakpoints-" + theme.Id + ".png"));
            var menu = debugMargin!.CreateBreakpointMenu(F407DebugExample.Call);
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
            menu.HorizontalOffset = -18000;
            try
            {
                menu.IsOpen = true; menu.UpdateLayout(); await Settle();
                Render(menu, Path.Combine(directory, "breakpoint-menu-" + theme.Id + ".png"));
            }
            finally { menu.IsOpen = false; }
        }
        ApplyTheme(ThemeService.Dark);
        DebugContinue_Click(this, new RoutedEventArgs()); await pendingOperation; await WaitStopped();
        Check(services.Debugger.Snapshot.Watches.Single(v => v.Name == "app_counter").Value == "3", "Conditional demo must stop in third loop");
        var table = (DataGrid)DebugTools.FindName("Breakpoints");
        Check(table.Items.Cast<BreakpointRow>().Single(b => b.Line == F407DebugExample.Call).HitCount == 3, "Arrival count missing in table");
        var log = (TextBox)DebugTools.FindName("BreakpointOutput");
        Check(log.Text.Contains("count=1, output=3", StringComparison.Ordinal) && log.Text.Contains("count=2, output=5", StringComparison.Ordinal), "Logpoint output missing");
        DebugTools.ShowBreakpointLog(); UpdateLayout(); await Settle();
        Render(this, Path.Combine(directory, "logpoints.png"));
        SourceEditor.TextArea.Caret.Line = F407DebugExample.Delay;
        DebugRunToCursor_Click(this, new RoutedEventArgs()); await pendingOperation; await WaitStopped();
        Check(services.Debugger.Snapshot.Frames[0].Line == F407DebugExample.Delay && services.Debugger.Breakpoints.All(b => !b.SessionOnly), "Run-to-cursor cleanup");
        Check(log.Text.Contains("count=3, output=7", StringComparison.Ordinal), "Coincident logpoint should still log at cursor");
        Width = 1100; Height = 720; DebugTools.ShowBreakpoints(); UpdateLayout(); await Settle();
        Check(BottomPanel.TranslatePoint(new Point(0, BottomPanel.ActualHeight), WindowRoot).Y <= WindowRoot.ActualHeight - 24, "Bottom panel clipped");
        Render(this, Path.Combine(directory, "special-breakpoints-minimum.png"));
        await CloseProjectAsync(CancellationToken.None); await Settle();
        Check(log.Text.Length == 0 && table.Items.Count == 0, "Project close did not clear logs/rules UI");
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: special breakpoint dialog roundtrip/errors; 3 distinct gutter markers; rule/type/arrival columns; conditional trigger and auto-continuing log output; same-line log + run-to-cursor; project cleanup; dark/light/minimum renders. Offline only.\n");
    }
}
