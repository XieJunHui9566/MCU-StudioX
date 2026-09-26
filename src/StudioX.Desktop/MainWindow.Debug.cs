namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using StudioX.Application;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

public partial class MainWindow
{
    private DebugMargin? debugMargin;
    private bool debugUiQueued, projectActionsBusy;
    private DebugState? lastStatusDebugState;
    private string? lastStatusDebugReason;
    private Task debugNavigationTask = Task.CompletedTask;
    private DebugSnapshot? lastDebugSnapshot;
    private readonly Dictionary<string, (EditorDocumentSession Session, TextAnchor Anchor)> debugAnchors = [];
    private readonly DispatcherTimer breakpointSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    private void InitializeDebugger()
    {
        debugMargin = new DebugMargin { ToolTip = "单击设置 / 删除断点 (F9)" };
        SourceEditor.TextArea.LeftMargins.Insert(0, debugMargin);
        debugMargin.ToggleRequested += line => _ = ToggleBreakpointAsync(line);
        debugMargin.SettingsRequested += line => EditCurrentBreakpoint(line);
        debugMargin.RunToCursorRequested += line => RunToCursor(line);
        services.Debugger.Changed += QueueDebugUpdate;
        services.Debugger.Output += message => _ = Dispatcher.BeginInvoke(() => DebugTools.AppendOutput(message));
        services.Debugger.BreakpointLog += message => _ = Dispatcher.BeginInvoke(() => DebugTools.AppendBreakpointLog(message));
        DebugTools.FrameSelected += level => _ = RunAsync(async token => { await services.Debugger.RefreshAsync(level, token); await NavigateSelectedDebugFrameAsync(); });
        DebugTools.WatchChanged += (expression, remove) => _ = RunAsync(token => services.Debugger.ChangeWatchAsync(expression, remove, token));
        DebugTools.BreakpointChanged += (id, enabled) => _ = RunAsync(token => services.Debugger.ChangeBreakpointAsync(id, enabled, token));
        DebugTools.BreakpointSettings += id =>
        {
            var point = services.Debugger.Breakpoints.FirstOrDefault(b => b.Id == id && !b.SessionOnly);
            if (point is not null) _ = EditBreakpointSettingsAsync(point.File, point.Line, point);
        };
        DebugTools.Navigate += location => _ = RunAsync(token => NavigateDebugSourceAsync(location, token));
        DebugTools.DisassemblyRequested += address => debugDisassemblyTask = ReadDebugDisassemblyAsync(address);
        DebugTools.FreeRtosRequested += () => debugRtosTask = ReadDebugFreeRtosAsync();
        DebugTools.FreeRtosView.CancelRequested += CancelFreeRtosRead;
        DebugTools.MemoryRequested += address => _ = RunAsync(async token =>
        {
            if (!uint.TryParse(address.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                throw new StudioXException("DEBUG_MEMORY", "请输入合法的十六进制地址。");
            DebugTools.SetMemory(value, await services.Debugger.ReadMemoryAsync(value, token));
        });
        if (sourceContextMenu is { } menu)
        {
            var toggle = new MenuItem { Header = "设置 / 删除断点", InputGestureText = "F9" };
            toggle.Click += DebugBreakpoint_Click; menu.Items.Insert(0, toggle);
            var enable = new MenuItem { Header = "启用 / 禁用断点", InputGestureText = "Ctrl+F9" };
            enable.Click += DebugEnableBreakpoint_Click; menu.Items.Insert(1, enable); menu.Items.Insert(2, new Separator());
            var settings = new MenuItem { Header = "条件 / 日志 / 临时断点…", InputGestureText = "Shift+F9" };
            settings.Click += DebugBreakpointSettings_Click; menu.Items.Insert(2, settings);
            var runTo = new MenuItem { Header = "运行到光标", InputGestureText = "Ctrl+F10" };
            runTo.Click += DebugRunToCursor_Click; menu.Items.Insert(3, runTo);
            SourceEditor.PreviewMouseRightButtonDown += (_, e) =>
            {
                var position = SourceEditor.GetPositionFromPoint(e.GetPosition(SourceEditor));
                if (position is { } p) SourceEditor.TextArea.Caret.Position = p;
            };
            menu.Opened += (_, _) =>
            {
                toggle.IsEnabled = enable.IsEnabled = settings.IsEnabled = CanEditBreakpoints;
                runTo.IsEnabled = CanEditBreakpoints && services.Debugger.State == DebugState.Stopped;
            };
        }
        breakpointSaveTimer.Tick += async (_, _) =>
        {
            breakpointSaveTimer.Stop();
            try { await PersistBreakpointLinesAsync(); }
            catch (Exception ex) { Log(ex.ToString()); Status.Text = "断点位置保存失败：" + ex.Message; }
        };
        RefreshDebugUi();
    }
    private void QueueDebugUpdate()
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (closed || debugUiQueued) return;
            debugUiQueued = true;
            _ = Dispatcher.BeginInvoke(async () =>
            {
                debugUiQueued = false; if (closed) return;
                RefreshDebugUi();
                if (services.Debugger.State == DebugState.Stopped && services.Debugger.Snapshot != lastDebugSnapshot)
                {
                    lastDebugSnapshot = services.Debugger.Snapshot;
                    debugNavigationTask = NavigateSelectedDebugFrameAsync();
                    try { await debugNavigationTask; } catch (Exception ex) { DebugTools.AppendOutput(ex.ToString()); }
                }
            }, DispatcherPriority.Background);
        });
    }
    private void RefreshDebugUi()
    {
        if (DebugTools is null) return;
        var debug = services.Debugger; var state = debug.State;
        if (!debug.IsActive)
        {
            if (RegistersTab.Visibility == Visibility.Visible || DebugTab.Visibility == Visibility.Visible) HideDebugLayout();
        }
        else if (RegistersTab.Visibility != Visibility.Visible || DebugTab.Visibility != Visibility.Visible) ShowDebugLayout();
        var stopped = state == DebugState.Stopped; var running = state == DebugState.Running;
        DebugStateText.Text = debug.Reason;
        var stateChanged = lastStatusDebugState != state || lastStatusDebugReason != debug.Reason;
        lastStatusDebugState = state; lastStatusDebugReason = debug.Reason;
        // 异步运行/暂停通知也要更新底部状态；结束与失败的详细结果由命令处理器显示。
        if (stateChanged && debug.IsActive) Status.Text = debug.Reason;
        var target = debug.IsActive && debug.IsHardware ? debug.HardwareTarget :
            downloadConfiguration is { } configuration ? DebugTargetProfile.Find(configuration.Device) : null;
        RegisterTitle.Text = target?.RegisterTitle ?? "目标寄存器";
        RegisterHint.Text = running ? "运行中 · 上次暂停快照" : debug.IsActive ? (debug.IsHardware ? "实机读数 · 变化值高亮" : "模拟数据 · 变化值高亮") : "暂停后读取";
        DebugModeBadge.Text = debug.IsActive ? (debug.IsHardware ? "实机 · " + debug.HardwareTargetName : "离线模拟 · 未连接芯片") : "未连接调试目标";
        RegisterGrid.ItemsSource = debug.Snapshot.Registers;
        RegisterGrid.Opacity = running ? .55 : 1;
        DebugTools.FreeRtosView.SetProject(debug.ProjectDirectory);
        if (state != DebugState.Stopped) CancelFreeRtosRead();
        DebugTools.Refresh(debug.Snapshot, debug.Breakpoints, debug.Watches, state, debug.IsHardware);
        UpdateDebugControls(); UpdateBreakpointAnchors(); RefreshDebugMarkers();
        if (activeEditor is not null) SourceEditor.IsReadOnly = activeEditor.Source.IsReadOnly || debug.IsActive;
    }
    private void UpdateDebugControls()
    {
        if (DebugStartButton is null) return;
        var debug = services.Debugger; var idle = !projectActionsBusy;
        var stc = IsStcSdccProject;
        DebugTopMenu.Visibility = DebugStartButton.Visibility = stc ? Visibility.Collapsed : Visibility.Visible;
        if (debugMargin is not null) debugMargin.Visibility = stc ? Visibility.Collapsed : Visibility.Visible;
        var stopped = debug.State == DebugState.Stopped && idle;
        DebugStartButton.IsEnabled = DebugStartMenu.IsEnabled = idle &&
            !stc && (debug.IsActive || debug.State == DebugState.Faulted ||
             (projectDirectory is not null && supportsDownload));
        ShowDebugMenu.IsEnabled = debug.IsActive;
        ShowFreeRtosMenu.IsEnabled = debug.IsActive;
        DebugContinueButton.IsEnabled = DebugContinueMenu.IsEnabled = stopped;
        DebugOverButton.IsEnabled = DebugOverMenu.IsEnabled = stopped;
        DebugIntoButton.IsEnabled = DebugIntoMenu.IsEnabled = stopped;
        DebugOutButton.IsEnabled = DebugOutMenu.IsEnabled = stopped && debug.Snapshot.Frames.Length > 1;
        DebugPauseButton.IsEnabled = DebugPauseMenu.IsEnabled = idle && debug.State == DebugState.Running;
        DebugResetButton.IsEnabled = DebugRefreshButton.IsEnabled = stopped;
        DebugStopButton.IsEnabled = idle && (debug.IsActive || debug.State == DebugState.Faulted);
        if (debugMargin is not null) { debugMargin.CanEdit = idle && CanEditBreakpoints; debugMargin.CanRunToCursor = idle && CanEditBreakpoints && debug.State == DebugState.Stopped; }
        DebugRunToMenu.IsEnabled = stopped;
        var available = projectDirectory is not null && idle && !debug.IsActive;
        BuildButton.IsEnabled = BuildMenu.IsEnabled = available;
        DownloadButton.IsEnabled = DownloadMenu.IsEnabled = available && supportsDownload;
        DownloadProbePicker.IsEnabled = DownloadSettingsButton.IsEnabled = DownloadSettingsMenu.IsEnabled = available && supportsDownload;
        UpdateBuildSettingsControls();
    }
    private void ShowDebugLayout()
    {
        RegistersTab.Visibility = Visibility.Visible;
        DebugTab.Visibility = Visibility.Visible;
        DebugToolbar.Visibility = Visibility.Visible;
        if (ProjectPanel.Visibility != Visibility.Visible) ToggleProject_Click(this, new RoutedEventArgs());
        LeftToolTabs.SelectedItem = RegistersTab;
        savedBottomHeight = Math.Clamp(ActualHeight * .31, 220, 300); ShowBottom(2);
    }
    private void HideDebugLayout()
    {
        if (LeftToolTabs.SelectedItem == RegistersTab) LeftToolTabs.SelectedIndex = 0;
        if (BottomTabs.SelectedItem == DebugTab) BottomTabs.SelectedIndex = 0;
        RegistersTab.Visibility = DebugTab.Visibility = DebugToolbar.Visibility = Visibility.Collapsed;
    }
    private void ShowDebug_Click(object sender, RoutedEventArgs e)
    {
        if (!services.Debugger.IsActive) return;
        ShowDebugLayout(); RefreshDebugUi();
    }
    private async void DebugExample_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (!await CloseProjectAsync(token)) return;
        var directory = await DebugSessionService.CreateExampleAsync(services.Packs, services.DataDirectory, Path.Combine(AppContext.BaseDirectory, "device-packs"), token);
        await OpenProjectAsync(directory, token);
        await services.Debugger.StartOfflineAsync(token);
        await NavigateSelectedDebugFrameAsync();
    });
    private async void DebugStart_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (services.Debugger.IsActive) { await services.Debugger.StopAsync(); Status.Text = "调试已结束，烧录器已释放。"; return; }
        if (services.Debugger.State == DebugState.Faulted) await services.Debugger.StopAsync();
        if (projectDirectory is null)
            throw new StudioXException("DEBUG_PROJECT", "请先打开支持调试的工程并选择烧录器。STM32F1/F4 使用 ST-Link 或 DAP；AG32VF303 使用官方 AGM BLASTER；CH32V203 / V307 与 CH592 / CH595 使用 WCH-Link；RP2350 使用 DAP 和包含调试配置的器件包。");
        var configuration = await services.Downloads.ConfigurationAsync(RequireProject(), token)
            ?? throw new StudioXException("DEBUG_TARGET", "当前工程缺少调试配置。");
        _ = OpenOcdDebugPlanner.ResolveProbe(configuration);
        ApplyDownloadConfiguration(configuration);
        await SaveAllSourcesAsync(RequireProject(), token); await PersistBreakpointLinesAsync();
        var build = await BuildWithSummaryAsync(RequireProject(), token);
        if (!build.Success) { Status.Text = "编译失败，未连接调试目标。"; return; }
        Status.Text = "准备实机调试：核对源码、ELF 与内置工具…";
        var preparation = await HardwareDebugPreparer.PrepareAsync(RequireProject(), services.Downloads, token);
        DebugTools.AppendOutput("会话日志：" + preparation.LogPath);
        await services.Debugger.StartHardwareAsync(preparation, token);
        await NavigateSelectedDebugFrameAsync();
        Status.Text = services.Debugger.Reason;
    });
    private async void DebugStop_Click(object sender, RoutedEventArgs e) => await RunAsync(async _ =>
    {
        CancelFreeRtosRead();
        await services.Debugger.StopAsync();
        Status.Text = "调试已结束，烧录器已释放。";
    });
    private void DebugContinue_Click(object sender, RoutedEventArgs e) => RunDebugAction(DebugAction.Continue);
    private void DebugPause_Click(object sender, RoutedEventArgs e) => RunDebugAction(DebugAction.Pause);
    private void DebugInto_Click(object sender, RoutedEventArgs e) => RunDebugAction(DebugAction.StepInto);
    private void DebugOver_Click(object sender, RoutedEventArgs e) => RunDebugAction(DebugAction.StepOver);
    private void DebugOut_Click(object sender, RoutedEventArgs e) => RunDebugAction(DebugAction.StepOut);
    private void DebugReset_Click(object sender, RoutedEventArgs e) => RunDebugAction(DebugAction.Reset);
    private void RunDebugAction(DebugAction action)
    {
        CancelFreeRtosRead();
        _ = RunAsync(token => services.Debugger.ExecuteAsync(action, token));
    }
    private async void DebugRefresh_Click(object sender, RoutedEventArgs e) => await RunAsync(token => services.Debugger.RefreshAsync(token: token));
    private void DebugBreakpoint_Click(object sender, RoutedEventArgs e) => _ = ToggleBreakpointAsync(SourceEditor.TextArea.Caret.Line);
    private void DebugBreakpointSettings_Click(object sender, RoutedEventArgs e) => EditCurrentBreakpoint(SourceEditor.TextArea.Caret.Line);
    private void DebugRunToCursor_Click(object sender, RoutedEventArgs e) => RunToCursor(SourceEditor.TextArea.Caret.Line);
    private void RunToCursor(int line)
    {
        if (!CanEditBreakpoints || services.Debugger.State != DebugState.Stopped) return;
        var file = activeDocument!.RelativePath;
        _ = RunAsync(token => services.Debugger.RunToCursorAsync(file, line, token));
    }
    private SourceBreakpoint? BreakpointAtViewLine(string file, int line) => services.Debugger.Breakpoints.FirstOrDefault(b => !b.SessionOnly &&
        ((b.BoundLocation is { } bound && bound.File.Equals(file, StringComparison.OrdinalIgnoreCase) && bound.Line == line) ||
         (b.File.Equals(file, StringComparison.OrdinalIgnoreCase) &&
          (debugAnchors.TryGetValue(b.Id, out var a) && !a.Anchor.IsDeleted ? a.Anchor.Line : b.Line) == line)));
    private void EditCurrentBreakpoint(int line)
    {
        if (!CanEditBreakpoints) return;
        var file = activeDocument!.RelativePath;
        _ = EditBreakpointSettingsAsync(file, line, BreakpointAtViewLine(file, line));
    }
    private Task EditBreakpointSettingsAsync(string file, int line, SourceBreakpoint? point) => RunAsync(async token =>
    {
        await PersistBreakpointLinesAsync();
        var dialog = new BreakpointWindow(file, line, point is null ? new() : BreakpointOptions.From(point)) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Options is not { } options) return;
        var savedLine = point is null ? line : services.Debugger.Breakpoints.FirstOrDefault(b => b.Id == point.Id)?.Line ?? line;
        await services.Debugger.ConfigureBreakpointAsync(file, savedLine, options, token);
        if (services.Debugger.IsActive) { ShowBottom(2); DebugTools.ShowBreakpoints(); }
        RefreshDebugUi(); Status.Text = "断点设置已保存。";
    });
    private async Task ToggleBreakpointAsync(int line)
    {
        if (!CanEditBreakpoints) return;
        var file = activeDocument!.RelativePath;
        var existing = BreakpointAtViewLine(file, line);
        await RunAsync(async token =>
        {
            await PersistBreakpointLinesAsync();
            if (existing is not null) await services.Debugger.ChangeBreakpointAsync(existing.Id, null, token);
            else await services.Debugger.ToggleBreakpointAsync(file, line, token);
            RefreshDebugUi();
        });
    }
    private async void DebugEnableBreakpoint_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditBreakpoints) return;
        var point = BreakpointAtViewLine(activeDocument!.RelativePath, SourceEditor.TextArea.Caret.Line);
        if (point is not null) await RunAsync(token => services.Debugger.ChangeBreakpointAsync(point.Id, !point.Enabled, token));
    }
    private bool CanEditBreakpoints => !IsStcSdccProject && projectDirectory is not null && activeDocument is not null && WorkspaceTabs.SelectedItem == activeEditor?.Tab &&
        !Path.IsPathRooted(activeDocument.RelativePath) && CodeLanguage.ForFile(activeDocument.RelativePath) is "C" or "C++" &&
        services.Debugger.State is DebugState.Disconnected or DebugState.Stopped or DebugState.Faulted;
    private async Task NavigateSelectedDebugFrameAsync()
    {
        var snapshot = services.Debugger.Snapshot;
        if (services.Debugger.State != DebugState.Stopped) return;
        var frame = snapshot.Frames.FirstOrDefault(f => f.Level == snapshot.SelectedFrame);
        if (frame is not null && frame.Line > 0) await NavigateDebugSourceAsync(new(frame.File, frame.Line), CancellationToken.None);
        else if (frame is not null) DebugTools.ShowDisassembly();
        RefreshDebugMarkers();
    }
    private async Task NavigateDebugSourceAsync(SourceLocation location, CancellationToken token)
    {
        var root = projectDirectory;
        if (root is null) return;
        var relative = Path.IsPathRooted(location.File) ? Path.GetRelativePath(root, location.File).Replace('\\', '/') : location.File;
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)) { Status.Text = "当前栈帧位于工程外部：" + location.File; if (services.Debugger.State == DebugState.Stopped) DebugTools.ShowDisassembly(); return; }
        _ = PathBoundary.Resolve(root, relative);
        if (!services.Files.FileExists(root, relative)) { Status.Text = "调试源码不存在：" + relative; if (services.Debugger.State == DebugState.Stopped) DebugTools.ShowDisassembly(); return; }
        var existing = FindEditor(relative);
        if (existing is null)
        {
            var source = await services.Files.ReadAsync(root, relative, token);
            if (root != projectDirectory || closing) return;
            // 两个暂停通知可能同时等待磁盘；不可重建已打开的文档和撤销栈。
            existing = FindEditor(relative) ?? AddEditor(source);
        }
        ShowDocument(existing.Tab);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (root != projectDirectory || activeEditor != existing || closing) return;
        var line = Math.Clamp(location.Line, 1, SourceEditor.Document.LineCount);
        SourceEditor.TextArea.Caret.Line = line; SourceEditor.TextArea.Caret.Column = 1;
        SourceEditor.ScrollTo(line, 1); RefreshDebugMarkers();
    }
    private void RefreshDebugMarkers()
    {
        if (debugMargin is null) return;
        var path = activeDocument?.RelativePath; var debug = services.Debugger;
        debugMargin.Breakpoints = debug.Breakpoints.Where(b => (b.BoundLocation?.File ?? b.File).Equals(path, StringComparison.OrdinalIgnoreCase)).Select(b =>
            b with { Line = b.BoundLocation?.Line ?? (debugAnchors.TryGetValue(b.Id, out var a) && !a.Anchor.IsDeleted ? a.Anchor.Line : b.Line) }).ToArray();
        var current = debug.Snapshot.Frames.FirstOrDefault(f => f.Level == 0);
        var selected = debug.Snapshot.Frames.FirstOrDefault(f => f.Level == debug.Snapshot.SelectedFrame);
        debugMargin.ExecutionLine = debug.State == DebugState.Stopped && current is not null && current.File == path ? current.Line : null;
        debugMargin.SelectedLine = debug.State == DebugState.Stopped && selected is not null && selected.File == path && selected.Level > 0 ? selected.Line : null;
        debugMargin.Refresh();
    }
    private void UpdateBreakpointAnchors()
    {
        foreach (var id in debugAnchors.Keys.ToArray())
            if (!services.Debugger.Breakpoints.Any(b => b.Id == id) || !editorDocuments.Contains(debugAnchors[id].Session)) debugAnchors.Remove(id);
        foreach (var b in services.Debugger.Breakpoints)
        {
            if (debugAnchors.ContainsKey(b.Id) || FindEditor(b.File) is not { } session || b.Line > session.Buffer.LineCount) continue;
            var anchor = session.Buffer.CreateAnchor(session.Buffer.GetLineByNumber(b.Line).Offset);
            anchor.MovementType = AnchorMovementType.AfterInsertion; anchor.SurviveDeletion = true; debugAnchors[b.Id] = (session, anchor);
        }
    }
    private Task PersistBreakpointLinesAsync()
    {
        breakpointSaveTimer.Stop();
        // 内存中跟随编辑，但只把已保存文件的位置落盘，放弃编辑不会留下偏移后的断点。
        var lines = debugAnchors.Where(p => !p.Value.Anchor.IsDeleted && !p.Value.Session.IsDirty).ToDictionary(p => p.Key, p => p.Value.Anchor.Line);
        if (!services.Debugger.Breakpoints.Any(b => lines.TryGetValue(b.Id, out var line) && b.Line != line)) return Task.CompletedTask;
        return services.Debugger.UpdateLinesAsync(lines);
    }
    private void DebugSourceChanged()
    {
        RefreshDebugMarkers(); if (changingEditor || services.Debugger.IsActive) return;
        breakpointSaveTimer.Stop(); breakpointSaveTimer.Start();
    }
    private void EnsureNoActiveDebug()
    {
        if (services.Debugger.IsActive) throw new StudioXException("DEBUG_BUSY", "请先结束调试，再编译或下载。");
    }
    private bool HandleDebugKey(KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.F9 && mods == ModifierKeys.None) DebugBreakpoint_Click(this, e);
        else if (e.Key == Key.F9 && mods == ModifierKeys.Shift) DebugBreakpointSettings_Click(this, e);
        else if (e.Key == Key.F9 && mods == ModifierKeys.Control) DebugEnableBreakpoint_Click(this, e);
        else if (e.Key == Key.F5 && mods == ModifierKeys.Control && DebugStartMenu.IsEnabled) DebugStart_Click(this, e);
        else if (services.Debugger.IsActive && e.Key == Key.F5 && mods == ModifierKeys.None) DebugContinue_Click(this, e);
        else if (services.Debugger.IsActive && e.Key == Key.F10 && mods == ModifierKeys.None) DebugOver_Click(this, e);
        else if (services.Debugger.IsActive && e.Key == Key.F10 && mods == ModifierKeys.Control) DebugRunToCursor_Click(this, e);
        else if (services.Debugger.IsActive && e.Key == Key.F11 && mods == ModifierKeys.None) DebugInto_Click(this, e);
        else if (services.Debugger.IsActive && e.Key == Key.F11 && mods == ModifierKeys.Control) DebugOut_Click(this, e);
        else return false;
        e.Handled = true; return true;
    }
}
