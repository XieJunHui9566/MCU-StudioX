namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.StcDebugging;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

public partial class MainWindow
{
    public async Task RenderMon51PreviewAsync(string directory, Mon51OfflineTransport fixture)
    {
        var checks = new List<string>();
        void Check(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
        }
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            RefreshDebugUi();
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        var root = Path.Combine(directory, "project");
        Directory.CreateDirectory(Path.Combine(root, ".studiox"));
        var manifest = new ProjectManifest(1, "Mon51Preview", "stc.mon51", "1.0.0", "", "IAP15F2K61S2", "bare-metal", "stc.sdcc", "1.0.0", "sdcc-4.5.0-15242");
        await JsonStore.WriteAsync(Path.Combine(root, ".studiox", "project.json"), manifest);
        projectDirectory = root;
        currentProjectManifest = manifest;
        ConfigureBuildSettingsForProject();
        ApplyBuildSettings(new());
        StcMon51Profile.IsChecked = true;
        Check(StcMon51Profile.Visibility == Visibility.Visible && SelectedBuildSettings is { Mon51Profile: true, DebugInfo: CompilerDebugInfo.Standard }, "Mon51 build profile forces native standard CDB on exact IAP target");
        SelectedBuildSettings.ValidateFor(manifest);
        ApplyBuildSettings(new());
        await services.Debugger.OpenProjectAsync(root);
        await File.WriteAllTextAsync(Path.Combine(root, "pending.c"), "void main(void)\n{\n}\n");
        await OpenSourceAsync("pending.c", CancellationToken.None);
        await services.Debugger.ToggleBreakpointAsync("pending.c", 2);
        await Settle();
        RefreshDebugUi();
        Check(DebugStartButton.Visibility == Visibility.Visible && DebugStartButton.IsEnabled && debugMargin!.Visibility == Visibility.Visible, "STC connection entry and source breakpoint gutter visible");
        Check(!Mon51BreakpointInteriorOpaque(2), "unverified saved source breakpoint renders hollow before connection");
        var connection = new Mon51ConnectionWindow(["COM14"], "COM14", "my_firmware", [Mon51PreviewFirmware],
            (_, _, _, _, _) => throw new InvalidOperationException("Preview must never open a serial port"),
            (_, _, _, _, _) => throw new InvalidOperationException("Preview must never install a monitor"))
        {
            Owner = this,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        connection.Show();
        connection.UpdateLayout();
        Render(connection, Path.Combine(directory, "connection.png"));
        connection.Close();
        await CheckMon51ConnectionRetryAsync(checks);
        await services.Debugger.StartMon51Async("COM14", 115200, "IAP15F2K61S2");
        await Settle();
        Check(Mon51Tools.Visibility == Visibility.Visible && GeneralDebugTools.Visibility == Visibility.Collapsed && RegisterTitle.Text == "8051 寄存器" && RegisterGrid.Items.Count == 19, "dedicated 8051 panel and register rows");
        Check(DebugContinueButton.IsEnabled && DebugIntoButton.IsEnabled && !DebugPauseButton.IsEnabled && DebugOverButton.Visibility == Visibility.Visible && !DebugOutButton.IsEnabled, "stopped toolbar exposes step-over and guards unverified caller");
        Check(!BuildButton.IsEnabled && !DownloadButton.IsEnabled && DebugModeBadge.Text.Contains("离线"), "active guards and explicit offline label");
        var monitor = services.Debugger.MonitorSession!;
        Check(monitor.SymbolsStatus.Contains("构建回执"), "connection exposes missing build reason in source status");
        await monitor.ChangeSourceBreakpointAsync(services.Debugger.Breakpoints.Single().Id, null);
        Mon51Tools.SetMemory(Mon51MemorySpace.DataSfr, 0x20, await monitor.ReadMemoryAsync(Mon51MemorySpace.DataSfr, 0x20, 64));
        await Settle();
        Render(this, Path.Combine(directory, "stopped.png"));
        await monitor.AddBreakpointAsync(0xa8);
        ((TabControl)Mon51Tools.FindName("Tabs")).SelectedIndex = 1;
        await Settle();
        Render(this, Path.Combine(directory, "address-breakpoints.png"));
        Check(((DataGrid)Mon51Tools.FindName("Points")).Items.Count == 1, "address breakpoint table bound");
        ApplyTheme(ThemeService.Light);
        await Settle();
        Render(this, Path.Combine(directory, "address-breakpoints-light.png"));
        ApplyTheme(ThemeService.Dark);
        Width = 1100;
        Height = 700;
        await Settle();
        Render(this, Path.Combine(directory, "minimum.png"));
        Check(Mon51Tools.ActualWidth > 600 && Mon51Tools.ActualHeight > 180, "minimum window preserves usable debug panel");
        Width = 1460;
        Height = 920;
        await services.Debugger.ExecuteAsync(DebugAction.Continue);
        await Settle();
        Check(DebugPauseButton.IsEnabled && !DebugContinueButton.IsEnabled && !DebugIntoButton.IsEnabled && !((Panel)Mon51Tools.FindName("MemoryControls")).IsEnabled && !((Panel)Mon51Tools.FindName("BreakpointControls")).IsEnabled, "running controls disable memory, PC and breakpoint writes");
        Render(this, Path.Combine(directory, "running.png"));
        fixture.SignalBreakpoint(0xa8);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
        {
            while (services.Debugger.State == DebugState.Running)
            {
                await Task.Delay(20, timeout.Token);
            }
        }
        await Settle();
        Check(Status.Text.Contains("地址断点命中") && fixture.Code[0xa8] == 0xe5, "asynchronous verified breakpoint updates visible status and restores opcode");
        await services.Debugger.ExecuteAsync(DebugAction.StepInto);
        await Settle();
        Check(RegisterGrid.Items.Cast<DebugRegister>().Single(r => r.Name == "PC").Changed, "PC changes highlighted after native step");
        await monitor.RemoveBreakpointAsync(0xa8);
        foreach (var file in new[] { "probe.c", "probe.cdb", "probe.ihx" })
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "mon51-fixtures", file), Path.Combine(root, file), true);
        }
        var image = await File.ReadAllBytesAsync(Path.Combine(root, "probe.ihx"));
        var (code, present) = StcDebugArtifacts.ParseImage(image);
        code.CopyTo(fixture.Code, 0);
        fixture.UseMemoryImage = true;
        fixture.DataRam[8] = 0xd1;
        fixture.DataRam[0x30] = 3;
        fixture.XdataRam[0x20] = 6;
        await monitor.LoadSymbolsAsync(new(root, Path.Combine(root, "probe.ihx"), Path.Combine(root, "probe.cdb"), await File.ReadAllTextAsync(Path.Combine(root, "probe.cdb")), code, present, "offline recorded fixture", "offline recorded fixture", "offline recorded fixture"));
        await monitor.SetPcAsync(0xa8);
        await services.Debugger.ChangeWatchAsync("counter", false);
        await services.Debugger.ChangeWatchAsync("total", false);
        await services.Debugger.ConfigureBreakpointAsync("probe.c", 7, new(Condition: "counter >= 3"));
        await OpenSourceAsync("probe.c", CancellationToken.None);
        await NavigateSelectedDebugFrameAsync();
        Mon51Tools.ShowSourceTools();
        DebugTools.ShowWatches();
        await Settle();
        Check(DebugOverButton.IsEnabled && DebugOutButton.IsEnabled && DebugRunToMenu.IsEnabled && CanEditBreakpoints && SourceEditor.IsReadOnly && debugMargin!.ExecutionLine == 7, "verified source controls and editor execution markers enabled");
        Check(Mon51BreakpointInteriorOpaque(7), "verified source breakpoint renders filled after image and CDB validation");
        Check(services.Debugger.Snapshot.Frames.Length == 2 && services.Debugger.Snapshot.Watches.Single(w => w.Name == "counter").Value.StartsWith("3 ("), "source watch values and verified callers visible");
        Render(this, Path.Combine(directory, "source-debugging.png"));
        Width = 1100;
        Height = 700;
        await Settle();
        Render(this, Path.Combine(directory, "source-minimum.png"));
        Check(Mon51Tools.SourceTools.ActualWidth > 500 && debugMargin!.ExecutionLine == 7, "minimum window retains source tools and execution marker");
        Width = 1460;
        Height = 920;
        DebugTools.ShowBreakpoints();
        await Settle();
        Render(this, Path.Combine(directory, "source-breakpoints.png"));
        DebugTools.ShowDisassembly();
        await debugDisassemblyTask;
        await Settle();
        Check(DebugTools.DisassemblyInstructionCount > 0 && DebugTools.DisassemblyCurrentAddress == 0xa8 && !DebugTools.DisassemblyStatus.Contains("GDB"), "Mon51 CODE disassembly routes to native decoder with honest source label");
        Render(this, Path.Combine(directory, "source-disassembly.png"));
        ApplyTheme(ThemeService.Light);
        await Settle();
        Render(this, Path.Combine(directory, "source-light.png"));
        ApplyTheme(ThemeService.Dark);
        await services.Debugger.StopAsync();
        await Settle();
        Check(RegistersTab.Visibility == Visibility.Collapsed && DebugTab.Visibility == Visibility.Collapsed && fixture.Running, "end restores layout and resumes program");
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), $"PASS {checks.Count} WPF Mon51 UI checks. Protocol fixture only; no hardware or serial access.\n" + string.Join('\n', checks));
    }

    private bool Mon51BreakpointInteriorOpaque(int line)
    {
        var margin = debugMargin!;
        var view = SourceEditor.TextArea.TextView;
        var visual = view.VisualLines.Single(value => value.FirstDocumentLine.LineNumber <= line && value.LastDocumentLine.LineNumber >= line);
        var y = (int)(visual.VisualTop - view.VerticalOffset + visual.Height / 2) + 2;
        var width = (int)Math.Ceiling(margin.ActualWidth);
        var height = (int)Math.Ceiling(margin.ActualHeight);
        var execution = margin.ExecutionLine;
        var selected = margin.SelectedLine;
        try
        {
            // 直接渲染边栏像素；暂时隐藏箭头，避免执行标记遮挡断点填充而使回归误通过。
            margin.ExecutionLine = margin.SelectedLine = null;
            margin.Refresh();
            margin.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(margin);
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            return pixels[(y * width + 12) * 4 + 3] > 128;
        }
        finally { margin.ExecutionLine = execution; margin.SelectedLine = selected; margin.Refresh(); }
    }

    public async Task CaptureMon51EntryAsync(string directory)
    {
        if (!IsStcSdccProject)
        {
            return;
        }
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        RefreshDebugUi();
        UpdateLayout();
        Directory.CreateDirectory(directory);
        Render(this, Path.Combine(directory, "mon51-entry.png"));
        await JsonStore.WriteAsync(Path.Combine(directory, "mon51-entry.json"), new
        {
            Project = projectDirectory,
            Entry = "调试 / Ctrl+F5",
            Visible = DebugStartButton.Visibility == Visibility.Visible,
            Enabled = DebugStartButton.IsEnabled,
            HardwareSession = services.Debugger.IsHardware,
            Active = services.Debugger.IsActive
        });
    }
}
