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
            tab.Padding = new Thickness(12, 4, 8, 4); tab.Height = 34;
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
        tab.PreviewMouseDown += async (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            e.Handled = true; await RunAsync(_ => CloseWorkspaceTabAsync(tab));
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
        session.Changed = (_, _) => { UpdateEditorHeader(session); ClearBuildDiagnostics(); CommandManager.InvalidateRequerySuggested(); };
        session.Buffer.TextChanged += session.Changed;
        editorDocuments.Add(session);
        WorkspaceTabs.Items.Insert(editorDocuments.Count, session.Tab);
        AttachTabMouseActions(session.Tab);
        UpdateEditorHeaders();
        return session;
    }

    private void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != WorkspaceTabs || changingEditor) return;
        CloseCodeAssistance();
        if (WorkspaceTabs.SelectedItem is TabItem { Tag: EditorDocumentSession session }) ActivateEditor(session);
        else CaptureEditorView();
        if (WorkspaceTabs.SelectedItem is TabItem selected)
            _ = Dispatcher.BeginInvoke(() => { if (WorkspaceTabs.SelectedItem == selected) selected.BringIntoView(); }, DispatcherPriority.Loaded);
    }

    private void CaptureEditorView()
    {
        if (activeEditor is not { } session || SourceEditor.Document != session.Buffer) return;
        session.CaretOffset = SourceEditor.CaretOffset;
        session.SelectionStart = SourceEditor.SelectionStart;
        session.SelectionLength = SourceEditor.SelectionLength;
        session.VerticalOffset = SourceEditor.VerticalOffset;
        session.HorizontalOffset = SourceEditor.HorizontalOffset;
    }

    private void ActivateEditor(EditorDocumentSession session)
    {
        if (activeEditor == session && SourceEditor.Document == session.Buffer) { SourceEditor.Focus(); return; }
        CaptureEditorView(); CloseCodeAssistance();
        changingEditor = true;
        try
        {
            if (activeEditor is not null) activeEditor.Tab.Content = null;
            activeEditor = session;
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
            ApplyEditorTheme(); UpdateEditorPosition(); RefreshDiagnosticMarkers();
            UpdateBreakpointAnchors(); RefreshDebugMarkers();
            var revision = ++editorViewRevision;
            SourceEditor.Focus();
            // 文档挂入选中标签后才有正确的滚动范围；过期的恢复任务不可影响后切换的文件。
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (revision != editorViewRevision || activeEditor != session || WorkspaceTabs.SelectedItem != session.Tab) return;
                SourceEditor.UpdateLayout();
                SourceEditor.ScrollToVerticalOffset(session.VerticalOffset);
                SourceEditor.ScrollToHorizontalOffset(session.HorizontalOffset);
            }, DispatcherPriority.Loaded);
        }
        finally { changingEditor = false; }
        QueueOutlineRefresh(clear: true);
    }

    private void UpdateEditorHeaders() { foreach (var session in editorDocuments) UpdateEditorHeader(session); }
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
        if (!session.IsDirty) return;
        // 保存的是捕获的标签及其文本快照，异步期间切换文件不会写错文件。
        var text = session.Buffer.Text;
        session.Source = await services.Files.SaveAsync(directory, session.Source, text, token);
        UpdateEditorHeader(session);
        await PersistBreakpointLinesAsync();
    }
    private async Task SaveAllSourcesAsync(string directory, CancellationToken token)
    {
        foreach (var session in editorDocuments.ToArray()) await SaveEditorAsync(directory, session, token);
    }
    private async void SaveAll_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        await SaveAllSourcesAsync(RequireProject(), token); Status.Text = "已保存所有修改的文件。";
    });

    private async Task<bool> ConfirmEditorAsync(EditorDocumentSession session, Func<SourceDocument, MessageBoxResult>? decide = null)
    {
        while (session.IsDirty)
        {
            var result = decide?.Invoke(session.Source) ?? MessageBox.Show(this, $"{session.Source.RelativePath} 尚未保存。\n是否保存后关闭？", "未保存的修改", MessageBoxButton.YesNoCancel, MessageBoxImage.None);
            if (result == MessageBoxResult.Cancel) return false;
            if (result == MessageBoxResult.No) return true;
            if (result != MessageBoxResult.Yes) return false;
            await SaveEditorAsync(RequireProject(), session, CancellationToken.None);
        }
        return true;
    }
    private async Task<bool> ConfirmDocumentsAsync(Func<SourceDocument, MessageBoxResult>? decide = null)
    {
        var wasEnabled = WorkspaceTabs.IsEnabled;
        WorkspaceTabs.IsEnabled = false;
        try
        {
            // 先收集全部决定，再移除标签；后面的取消会保留前面选择不保存的内存副本。
            foreach (var session in editorDocuments.ToArray()) if (!await ConfirmEditorAsync(session, decide)) return false;
            return true;
        }
        finally { WorkspaceTabs.IsEnabled = wasEnabled; }
    }

    private async Task CloseWorkspaceTabAsync(TabItem tab, Func<SourceDocument, MessageBoxResult>? decide = null)
    {
        if (tab.Tag is EditorDocumentSession session)
        {
            if (!editorDocuments.Contains(session) || !await ConfirmEditorAsync(session, decide)) return;
            var selected = WorkspaceTabs.SelectedItem == tab;
            var index = editorDocuments.IndexOf(session);
            var neighbor = editorDocuments.Count > 1 ? editorDocuments[index > 0 ? index - 1 : 1].Tab : null;
            RemoveEditor(session);
            if (selected) ShowDocument(neighbor ?? WelcomeTab);
        }
        else
        {
            if (tab == SerialTab) await SerialView.CloseSessionAsync();
            if (tab == SerialPlotTab) await SerialPlotView.CloseSessionAsync();
            var selected = WorkspaceTabs.SelectedItem == tab;
            tab.Visibility = Visibility.Collapsed;
            if (selected) WorkspaceTabs.SelectedItem = editorDocuments.LastOrDefault()?.Tab ?? WorkspaceTabs.Items.OfType<TabItem>().FirstOrDefault(item => item.Visibility == Visibility.Visible);
        }
    }
    private void RemoveEditor(EditorDocumentSession session)
    {
        var wasChanging = changingEditor; changingEditor = true;
        try
        {
            if (activeEditor == session)
            {
                CloseCodeAssistance(); editorViewRevision++;
                session.Tab.Content = null; activeEditor = null;
                SourceEditor.Document = new TextDocument(); SourceEditor.IsReadOnly = true;
                RefreshDiagnosticMarkers();
            }
            if (session.Changed is not null) session.Buffer.TextChanged -= session.Changed;
            editorDocuments.Remove(session); WorkspaceTabs.Items.Remove(session.Tab);
            UpdateEditorHeaders();
        }
        finally { changingEditor = wasChanging; }
        if (!changingEditor && WorkspaceTabs.SelectedItem is TabItem { Tag: EditorDocumentSession selected }) ActivateEditor(selected);
    }
    private void ClearEditorDocuments()
    {
        ClearBuildDiagnostics();
        CancelSymbolRequests(); navigationBack.Clear(); navigationForward.Clear();
        changingEditor = true;
        try { foreach (var session in editorDocuments.ToArray()) RemoveEditor(session); }
        finally { changingEditor = false; }
    }
    private async void CloseDocument_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceTabs.SelectedItem is TabItem tab) await RunAsync(_ => CloseWorkspaceTabAsync(tab));
    }
    private void CycleEditor(bool backwards)
    {
        if (editorDocuments.Count == 0) return;
        var index = editorDocuments.FindIndex(session => session.Tab == WorkspaceTabs.SelectedItem);
        var next = index < 0 ? (backwards ? editorDocuments.Count - 1 : 0) : (index + (backwards ? -1 : 1) + editorDocuments.Count) % editorDocuments.Count;
        ShowDocument(editorDocuments[next].Tab);
    }
}
