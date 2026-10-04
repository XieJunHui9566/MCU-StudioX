namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using StudioX.Application;

public partial class MainWindow
{
    private readonly List<EditorDocumentSession> editorDocuments = [];
    private EditorDocumentSession? activeEditor;
    private TabItem? EditorTab => activeEditor?.Tab;
    private bool changingEditor;
    private long editorViewRevision;

    private void InitializeDocumentTabs()
    {
        EditorPlaceholder.Content = null;
        WorkspaceTabs.Items.Remove(EditorPlaceholder);
        foreach (var tab in WorkspaceTabs.Items.OfType<TabItem>())
        {
            var label = new TextBlock { Text = tab.Header?.ToString(), VerticalAlignment = VerticalAlignment.Center };
            tab.Header = CreateTabHeader(tab, label);
            tab.Padding = new Thickness(12, 4, 8, 4);
            tab.Height = 34;
            AttachTabMouseActions(tab);
        }
        WorkspaceTabs.SelectionChanged += WorkspaceTabs_SelectionChanged;
    }

    private StackPanel CreateTabHeader(TabItem tab, FrameworkElement label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(label);
        var icon = new Icon { Kind = "close", Width = 15, Height = 15 };
        icon.SetBinding(StudioX.Desktop.Icon.ForegroundProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        var close = new Button { Content = icon, Style = (Style)FindResource("DocumentTabCloseButton"), ToolTip = "关闭标签 (Ctrl+W)", Tag = tab };
        AutomationProperties.SetName(close, "关闭标签");
        close.Click += async (_, e) => { e.Handled = true; await RunAsync(_ => CloseWorkspaceTabAsync(tab)); };
        row.Children.Add(close);
        return row;
    }

    private void AttachTabMouseActions(TabItem tab)
    {
        AttachEditorDrag(tab);
        tab.PreviewMouseDown += async (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle)
            {
                return;
            }
            e.Handled = true;
            await RunAsync(_ => CloseWorkspaceTabAsync(tab));
        };
    }

    private EditorDocumentSession? FindEditor(string path) => editorDocuments.FirstOrDefault(session => string.Equals(session.Source.RelativePath, path, StringComparison.OrdinalIgnoreCase));
    private EditorDocumentSession AddEditor(SourceDocument source)
    {
        var session = new EditorDocumentSession(source);
        session.Tab.Tag = session;
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(new FileIcon { FileName = source.RelativePath, Width = 19, Height = 19, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(session.Label);
        session.Tab.Header = CreateTabHeader(session.Tab, label);
        session.Changed = (_, _) => { UpdateEditorHeader(session); guideSaved = false; guideBuild = null; RefreshFirstProjectGuide(); QueuePluginDocumentEvent("document.changed", session); services.Intelligence.InvalidateDiagnostics(); HideSymbolHover(); ClearBuildDiagnostics(); agentEditorSession?.InvalidateValidation(); QueueLiveDiagnostics(); CommandManager.InvalidateRequerySuggested(); };
        session.Buffer.TextChanged += session.Changed;
        editorDocuments.Add(session);
        WorkspaceTabs.Items.Insert(editorDocuments.Count(e => e.Group == 0), session.Tab);
        AttachTabMouseActions(session.Tab);
        UpdateEditorHeaders();
        QueuePluginDocumentEvent("document.opened", session);
        return session;
    }

    private void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != WorkspaceTabs && e.Source != secondaryTabs || changingEditor)
        {
            return;
        }
        RefreshPluginActivitySelection();
        CloseCodeAssistance();
        var tabs = (TabControl)e.Source;
        if (tabs.SelectedItem is TabItem { Tag: EditorDocumentSession session })
        {
            ActivateEditor(session);
        }
        else
        {
            CaptureEditorView();
        }
        if (tabs.SelectedItem is TabItem selected)
        {
            if (ReferenceEquals(selected, firstProjectTab))
            {
                RefreshFirstProjectGuide();
            }
            _ = Dispatcher.BeginInvoke(() => { if (tabs.SelectedItem == selected) { selected.BringIntoView(); } }, DispatcherPriority.Loaded);
        }
    }

    private void CaptureEditorView()
    {
        if (activeEditor is not { } session || SourceEditor.Document != session.Buffer)
        {
            return;
        }
        session.CaretOffset = SourceEditor.CaretOffset;
        session.SelectionStart = SourceEditor.SelectionStart;
        session.SelectionLength = SourceEditor.SelectionLength;
        session.VerticalOffset = SourceEditor.VerticalOffset;
        session.HorizontalOffset = SourceEditor.HorizontalOffset;
    }

    private void ActivateEditor(EditorDocumentSession session)
    {
        if (activeEditor == session && SourceEditor.Document == session.Buffer)
        {
            RefreshSplitMirror();
            SourceEditor.Focus();
            return;
        }
        CaptureEditorView();
        CloseCodeAssistance();
        changingEditor = true;
        try
        {
            if (activeEditor is not null)
            {
                activeEditor.Tab.Content = null;
            }
            activeEditor = session;
            if (mirroredSession == session)
            {
                session.Tab.Content = null;
                mirroredSession = null;
            }
            session.Tab.Content = EditorSurface;
            SourceEditor.Document = session.Buffer;
            SourceEditor.IsReadOnly = session.Source.IsReadOnly || services.Debugger.IsActive;
            var start = Math.Clamp(session.SelectionStart, 0, session.Buffer.TextLength);
            SourceEditor.Select(start, Math.Clamp(session.SelectionLength, 0, session.Buffer.TextLength - start));
            SourceEditor.CaretOffset = Math.Clamp(session.CaretOffset, 0, session.Buffer.TextLength);
            var document = session.Source;
            EditorBreadcrumb.Text = document.RelativePath.Replace("/", "  ›  ");
            EditorBreadcrumb.ToolTip = document.RelativePath;
            EditorLanguage.Text = $"{CodeLanguage.ForFile(document.RelativePath)}    ·    {document.Encoding.WebName.ToUpperInvariant()}    ·    {(session.Buffer.Text.Contains("\r\n") ? "CRLF" : "LF")}" + (document.IsReadOnly ? "    ·    " + (document.ReadOnlyReason ?? "只读") : "");
            StatusLanguage.Text = IsMicroPythonProject && CodeLanguage.ForFile(document.RelativePath) == "Python" ? "MicroPython" : CodeLanguage.ForFile(document.RelativePath);
            StatusEncoding.Text = document.Encoding.WebName.ToUpperInvariant();
            ApplyEditorTheme();
            UpdateEditorPosition();
            RefreshDiagnosticMarkers();
            UpdateBreakpointAnchors();
            RefreshDebugMarkers();
            var revision = ++editorViewRevision;
            SourceEditor.Focus();
            // 文档挂入选中标签后才有正确的滚动范围；过期的恢复任务不可影响后切换的文件。
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (revision != editorViewRevision || activeEditor != session || DocumentTabs(session).SelectedItem != session.Tab)
                {
                    return;
                }
                SourceEditor.UpdateLayout();
                SourceEditor.ScrollToVerticalOffset(session.VerticalOffset);
                SourceEditor.ScrollToHorizontalOffset(session.HorizontalOffset);
            }, DispatcherPriority.Loaded);
        }
        finally { changingEditor = false; }
        RefreshSplitMirror();
        QueueOutlineRefresh(clear: true);
        QueueLiveDiagnostics();
    }

    private void UpdateEditorHeaders()
    {
        foreach (var session in editorDocuments)
        {
            UpdateEditorHeader(session);
        }
    }
    private void UpdateEditorHeader(EditorDocumentSession session)
    {
        var name = Path.GetFileName(session.Source.RelativePath);
        var duplicate = editorDocuments.Count(other => Path.GetFileName(other.Source.RelativePath).Equals(name, StringComparison.OrdinalIgnoreCase)) > 1;
        var directory = Path.GetDirectoryName(session.Source.RelativePath)?.Replace('\\', '/');
        session.Label.Text = name + (duplicate ? " — " + (string.IsNullOrEmpty(directory) ? "根目录" : directory) : "") + (session.Source.IsReadOnly ? " [只读]" : "") + (session.IsDirty ? " •" : "");
        session.Tab.ToolTip = session.Source.RelativePath + (session.IsDirty ? "（未保存）" : "") + (session.Source.ReadOnlyReason is { } reason ? "\n" + reason : "");
        AutomationProperties.SetName(session.Tab, session.Label.Text);
    }

    private async Task SaveEditorAsync(string directory, EditorDocumentSession session, CancellationToken token)
    {
        if (!session.IsDirty)
        {
            return;
        }
        // 保存的是捕获的标签及其文本快照，异步期间切换文件不会写错文件。
        var text = session.Buffer.Text;
        session.Source = await services.Files.SaveAsync(directory, session.Source, text, token);
        guideSaved = true;
        guideBuild = null;
        RefreshFirstProjectGuide();
        QueuePluginDocumentEvent("document.saved", session);
        UpdateEditorHeader(session);
        await PersistBreakpointLinesAsync();
    }
    private async Task SaveAllSourcesAsync(string directory, CancellationToken token)
    {
        var savePinPlan = ag32PinPlanProject == directory && Ag32PinMapping.Planner.HasChanges;
        if (savePinPlan && Ag32PinMapping.Planner.Snapshot is { } snapshot && FindEditor(snapshot.SourcePath)?.IsDirty == true)
        {
            throw new StudioX.Foundation.StudioXException("AG32_PIN_PLAN_DIRTY",
                "VE 文本和图形配置同时有修改；请先单独保存 VE 并重新读取图形配置，再保存全部文件。");
        }
        foreach (var session in editorDocuments.ToArray())
        {
            await SaveEditorAsync(directory, session, token);
        }
        if (savePinPlan)
        {
            await SaveAg32PinPlanAsync(directory, token);
        }
    }
    private async void SaveAll_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        await SaveAllSourcesAsync(RequireProject(), token);
        Status.Text = "已保存所有修改的文件。";
    });

    private async Task<bool> ConfirmEditorAsync(EditorDocumentSession session, Func<SourceDocument, MessageBoxResult>? decide = null)
    {
        while (session.IsDirty)
        {
            var result = decide?.Invoke(session.Source) ?? MessageBox.Show(this, $"{session.Source.RelativePath} 尚未保存。\n是否保存后关闭？", "未保存的修改", MessageBoxButton.YesNoCancel, MessageBoxImage.None);
            if (result == MessageBoxResult.Cancel)
            {
                return false;
            }
            if (result == MessageBoxResult.No)
            {
                return true;
            }
            if (result != MessageBoxResult.Yes)
            {
                return false;
            }
            await SaveEditorAsync(RequireProject(), session, CancellationToken.None);
        }
        return true;
    }
    private async Task<bool> ConfirmDocumentsAsync(Func<SourceDocument, MessageBoxResult>? decide = null, bool retainEditorDrafts = false)
    {
        var wasEnabled = WorkspaceTabs.IsEnabled;
        WorkspaceTabs.IsEnabled = false;
        try
        {
            // 先收集全部决定，再移除标签；后面的取消会保留前面选择不保存的内存副本。
            if (ag32PinPlanProject == projectDirectory && Ag32PinMapping.Planner.HasChanges)
            {
                var choice = MessageBox.Show(this, "AG32 图形引脚配置尚未保存。\n是否保存并生成约束后关闭？",
                    "未保存的引脚配置", MessageBoxButton.YesNoCancel, MessageBoxImage.None);
                if (choice == MessageBoxResult.Cancel)
                {
                    return false;
                }
                if (choice == MessageBoxResult.Yes)
                {
                    await SaveAg32PinPlanAsync(RequireProject(), CancellationToken.None);
                }
            }
            foreach (var session in retainEditorDrafts ? Array.Empty<EditorDocumentSession>() : editorDocuments.ToArray())
            {
                if (!await ConfirmEditorAsync(session, decide))
                {
                    return false;
                }
            }
            return true;
        }
        finally { WorkspaceTabs.IsEnabled = wasEnabled; }
    }

    private async Task CloseWorkspaceTabAsync(TabItem tab, Func<SourceDocument, MessageBoxResult>? decide = null)
    {
        if (await ClosePluginDebugViewAsync(tab))
        {
            return;
        }
        if (tab.Tag is EditorDocumentSession session)
        {
            if (!editorDocuments.Contains(session) || !await ConfirmEditorAsync(session, decide))
            {
                return;
            }
            var selected = DocumentTabs(session).SelectedItem == tab;
            var index = editorDocuments.IndexOf(session);
            var neighbor = editorDocuments.Count > 1 ? editorDocuments[index > 0 ? index - 1 : 1].Tab : null;
            RemoveEditor(session);
            if (selected)
            {
                ShowDocument(neighbor ?? WelcomeTab);
            }
            // 显式关闭标签后的现场立即持久化，防止下次恢复重新出现已放弃的草稿。
            await editorCheckpointTask;
            await PersistEditorCheckpointAsync();
        }
        else
        {
            if (tab == SerialTab)
            {
                await SerialView.CloseSessionAsync();
            }
            if (tab == MicroPythonTab)
            {
                await MicroPythonPanel.StopAsync();
            }
            if (tab == SerialPlotTab)
            {
                await SerialPlotView.CloseSessionAsync();
            }
            if (tab == OpenOcdPlotTab)
            {
                await OpenOcdPlotPanel.CloseSessionAsync();
            }
            var selected = WorkspaceTabs.SelectedItem == tab;
            tab.Visibility = Visibility.Collapsed;
            if (selected)
            {
                WorkspaceTabs.SelectedItem = editorDocuments.LastOrDefault()?.Tab ?? WorkspaceTabs.Items.OfType<TabItem>().FirstOrDefault(item => item.Visibility == Visibility.Visible);
            }
        }
    }
    private void RemoveEditor(EditorDocumentSession session)
    {
        services.Intelligence.InvalidateDiagnostics();
        QueuePluginDocumentEvent("document.closed", session);
        var wasChanging = changingEditor;
        changingEditor = true;
        try
        {
            if (activeEditor == session)
            {
                CloseCodeAssistance();
                editorViewRevision++;
                session.Tab.Content = null;
                activeEditor = null;
                SourceEditor.Document = new TextDocument();
                SourceEditor.IsReadOnly = true;
                RefreshDiagnosticMarkers();
            }
            if (session.Changed is not null)
            {
                session.Buffer.TextChanged -= session.Changed;
            }
            editorDocuments.Remove(session);
            session.Tab.Content = null;
            DocumentTabs(session).Items.Remove(session.Tab);
            if (mirroredSession == session)
            {
                mirroredSession = null;
                mirrorEditor.Document = new TextDocument();
            }
            UpdateSplitVisibility();
            UpdateEditorHeaders();
        }
        finally { changingEditor = wasChanging; }
        var selected = (DocumentTabs(session).SelectedItem as TabItem)?.Tag as EditorDocumentSession
            ?? (WorkspaceTabs.SelectedItem as TabItem)?.Tag as EditorDocumentSession
            ?? (secondaryTabs?.SelectedItem as TabItem)?.Tag as EditorDocumentSession;
        if (!changingEditor && activeEditor is null && selected is not null)
        {
            ActivateEditor(selected);
        }
        RefreshSplitMirror();
        RefreshDiagnosticMarkers();
        QueueLiveDiagnostics();
    }
    private void ClearEditorDocuments()
    {
        ClearBuildDiagnostics();
        CancelSymbolRequests();
        navigationBack.Clear();
        navigationForward.Clear();
        changingEditor = true;
        try
        {
            foreach (var session in editorDocuments.ToArray())
            {
                RemoveEditor(session);
            }
        }
        finally { changingEditor = false; }
    }
    private async void CloseDocument_Click(object sender, RoutedEventArgs e)
    {
        if ((activeEditor is not null && SourceEditor.IsKeyboardFocusWithin ? activeEditor.Tab : WorkspaceTabs.SelectedItem) is TabItem tab)
        {
            await RunAsync(_ => CloseWorkspaceTabAsync(tab));
        }
    }
    private void CycleEditor(bool backwards)
    {
        if (editorDocuments.Count == 0)
        {
            return;
        }
        var index = activeEditor is null ? -1 : editorDocuments.IndexOf(activeEditor);
        var next = index < 0 ? (backwards ? editorDocuments.Count - 1 : 0) : (index + (backwards ? -1 : 1) + editorDocuments.Count) % editorDocuments.Count;
        ShowDocument(editorDocuments[next].Tab);
    }
}
