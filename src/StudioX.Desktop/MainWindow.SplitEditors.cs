namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StudioX.Application.Editing;

public partial class MainWindow
{
    private TabControl? secondaryTabs;
    private Grid? editorGroups;
    private readonly SafeTextEditor mirrorEditor = new() { ShowLineNumbers = true, IsReadOnly = true, Padding = new(12), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private EditorDocumentSession? mirroredSession;
    private double splitRatio = .5;
    private bool movingEditor;
    private EditorDocumentSession? dragCandidate;
    private Point dragStart;
    private TabControl DocumentTabs(EditorDocumentSession session) => session.Group == 1 && secondaryTabs is not null ? secondaryTabs : WorkspaceTabs;
    private bool IsActiveSourceTab => activeEditor is { } session && DocumentTabs(session).SelectedItem == session.Tab;

    private void InitializeSplitEditors()
    {
        var parent = (Grid)WorkspaceTabs.Parent;
        var row = Grid.GetRow(WorkspaceTabs);
        var column = Grid.GetColumn(WorkspaceTabs);
        parent.Children.Remove(WorkspaceTabs);
        editorGroups = new Grid();
        Grid.SetRow(editorGroups, row);
        Grid.SetColumn(editorGroups, column);
        editorGroups.ColumnDefinitions.Add(new());
        editorGroups.ColumnDefinitions.Add(new()
        {
            Width = new(0)
        });
        editorGroups.ColumnDefinitions.Add(new()
        {
            Width = new(0)
        });
        editorGroups.Children.Add(WorkspaceTabs);
        var splitter = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        splitter.SetResourceReference(BackgroundProperty, "Border");
        Grid.SetColumn(splitter, 1);
        editorGroups.Children.Add(splitter);
        secondaryTabs = new TabControl { Visibility = Visibility.Collapsed, AllowDrop = true };
        secondaryTabs.SetResourceReference(StyleProperty, "WorkspaceTabControl");
        Grid.SetColumn(secondaryTabs, 2);
        editorGroups.Children.Add(secondaryTabs);
        parent.Children.Add(editorGroups);
        secondaryTabs.SelectionChanged += WorkspaceTabs_SelectionChanged;
        mirrorEditor.SetResourceReference(BackgroundProperty, "EditorSurface");
        mirrorEditor.SetResourceReference(ForegroundProperty, "Text");
        mirrorEditor.PreviewMouseDown += (_, e) =>
        {
            if (mirroredSession is not { } session || movingEditor)
            {
                return;
            }
            var position = mirrorEditor.GetPositionFromPoint(e.GetPosition(mirrorEditor));
            var offset = position is null ? session.CaretOffset : session.Buffer.GetOffset(position.Value.Location);
            e.Handled = true;
            ShowDocument(session.Tab);
            SourceEditor.CaretOffset = Math.Clamp(offset, 0, session.Buffer.TextLength);
            SourceEditor.Focus();
        };
        mirrorEditor.GotKeyboardFocus += (_, _) => { if (!movingEditor && mirroredSession is { } session) { ShowDocument(session.Tab); } };
        WorkspaceTabs.AllowDrop = true;
        WorkspaceTabs.PreviewDrop += (_, e) => DropEditor(e, 0);
        secondaryTabs.PreviewDrop += (_, e) => DropEditor(e, 1);
        void DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetData("StudioX.EditorTab") is EditorDocumentSession session && editorDocuments.Contains(session))
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
            }
        }
        WorkspaceTabs.PreviewDragOver += DragOver;
        secondaryTabs.PreviewDragOver += DragOver;
        PreviewMouseLeftButtonUp += (_, _) => dragCandidate = null;
        PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                dragCandidate = null;
                return;
            }
            if (dragCandidate is not { } session || Math.Abs(e.GetPosition(this).X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(e.GetPosition(this).Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }
            dragCandidate = null;
            DragDrop.DoDragDrop(session.Tab, new DataObject("StudioX.EditorTab", session), DragDropEffects.Move);
        };
    }
    private void AttachEditorDrag(TabItem tab)
    {
        tab.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // 仅标签头触发拖动，不把编辑器中的文字选区当成标签移动。
            var point = e.GetPosition(tab);
            if (tab.Tag is not EditorDocumentSession session)
            {
                return;
            }
            if (point.Y < 0 || point.Y > tab.ActualHeight)
            {
                return;
            }
            dragStart = e.GetPosition(this);
            dragCandidate = session;
        };
    }
    private void DropEditor(DragEventArgs e, int group)
    {
        if (e.Data.GetData("StudioX.EditorTab") is not EditorDocumentSession session || !editorDocuments.Contains(session))
        {
            return;
        }
        var hit = e.OriginalSource as DependencyObject;
        while (hit is not null && hit is not TabItem)
        {
            hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
        }
        var before = (hit as TabItem)?.Tag as EditorDocumentSession;
        MoveEditorGroup(session, group, before);
        e.Handled = true;
    }
    private void MoveEditorGroup(EditorDocumentSession session, int group, EditorDocumentSession? before = null)
    {
        if (secondaryTabs is null || movingEditor)
        {
            return;
        }
        CaptureEditorView();
        movingEditor = true;
        changingEditor = true;
        try
        {
            DocumentTabs(session).Items.Remove(session.Tab);
            session.Group = group == 1 ? 1 : 0;
            editorDocuments.Remove(session);
            var index = before is not null && editorDocuments.Contains(before) ? editorDocuments.IndexOf(before) : editorDocuments.Count;
            editorDocuments.Insert(index, session);
            var target = DocumentTabs(session);
            if (before is not null && before.Group == session.Group && target.Items.Contains(before.Tab))
            {
                target.Items.Insert(target.Items.IndexOf(before.Tab), session.Tab);
            }
            else
            {
                target.Items.Add(session.Tab);
            }
            target.SelectedItem = session.Tab;
            UpdateSplitVisibility();
        }
        finally { changingEditor = false; movingEditor = false; }
        ShowDocument(session.Tab);
        RefreshSplitMirror();
        QueueEditorCheckpoint();
    }
    private void MergeEditorGroups()
    {
        foreach (var session in editorDocuments.Where(e => e.Group == 1).ToArray())
        {
            MoveEditorGroup(session, 0);
        }
        UpdateSplitVisibility();
    }
    private void UpdateSplitVisibility()
    {
        if (secondaryTabs is null || editorGroups is null)
        {
            return;
        }
        var show = secondaryTabs.Items.Count > 0;
        secondaryTabs.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        editorGroups.ColumnDefinitions[0].Width = new(show ? splitRatio : 1, GridUnitType.Star);
        editorGroups.ColumnDefinitions[1].Width = new(show ? 5 : 0);
        editorGroups.ColumnDefinitions[2].Width = show ? new(1 - splitRatio, GridUnitType.Star) : new(0);
    }
    private void RefreshSplitMirror()
    {
        if (secondaryTabs is null || movingEditor)
        {
            return;
        }
        foreach (var session in editorDocuments.Where(s => ReferenceEquals(s.Tab.Content, mirrorEditor)))
        {
            session.Tab.Content = null;
        }
        mirroredSession = null;
        RefreshInactiveCode();
        var other = activeEditor?.Group == 1 ? WorkspaceTabs : secondaryTabs;
        if (other.SelectedItem is not TabItem { Tag: EditorDocumentSession candidate } || candidate == activeEditor)
        {
            return;
        }
        mirroredSession = candidate;
        mirrorEditor.Document = candidate.Buffer;
        mirrorEditor.FontFamily = SourceEditor.FontFamily;
        mirrorEditor.FontSize = SourceEditor.FontSize;
        var color = (Color)ColorConverter.ConvertFromString(currentTheme.Colors["Background"]);
        mirrorEditor.SyntaxHighlighting = CodeLanguage.Get(CodeLanguage.ForFile(candidate.Source.RelativePath), color.R + color.G + color.B < 420);
        RefreshInactiveCode();
        candidate.Tab.Content = mirrorEditor;
        mirrorEditor.CaretOffset = Math.Clamp(candidate.CaretOffset, 0, candidate.Buffer.TextLength);
        _ = Dispatcher.BeginInvoke(() => { if (mirroredSession == candidate) { mirrorEditor.ScrollToVerticalOffset(candidate.VerticalOffset); } }, DispatcherPriority.Loaded);
    }
    private WorkbenchLayout CaptureWorkbenchLayout()
    {
        if (secondaryTabs?.Items.Count > 0 && editorGroups is not null)
        {
            splitRatio = editorGroups.ColumnDefinitions[0].ActualWidth / Math.Max(1, editorGroups.ColumnDefinitions[0].ActualWidth + editorGroups.ColumnDefinitions[2].ActualWidth);
        }
        return new(WindowState == WindowState.Normal ? ActualWidth : RestoreBounds.Width, WindowState == WindowState.Normal ? ActualHeight : RestoreBounds.Height,
            WindowState == WindowState.Maximized, ProjectPanel.Visibility == Visibility.Visible ? ProjectColumn.ActualWidth : savedProjectWidth,
            BottomPanel.Visibility == Visibility.Visible ? BottomRow.ActualHeight : savedBottomHeight, splitRatio,
            ProjectPanel.Visibility == Visibility.Visible, BottomPanel.Visibility == Visibility.Visible, outlinePreferred);
    }
    private async Task LoadWorkbenchLayoutAsync()
    {
        var layout = await services.WorkbenchLayout.LoadAsync();
        Width = Math.Min(layout.Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(layout.Height, SystemParameters.WorkArea.Height);
        if (layout.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
        savedProjectWidth = layout.ProjectWidth;
        savedBottomHeight = layout.BottomHeight;
        splitRatio = layout.SplitRatio;
        outlinePreferred = layout.OutlineVisible;
        SetOutlineVisible(outlinePreferred && EditorSurface.ActualWidth >= 650, false);
        ProjectPanel.Visibility = layout.ProjectVisible ? Visibility.Visible : Visibility.Collapsed;
        ProjectColumn.Width = new(layout.ProjectVisible ? layout.ProjectWidth : 0);
        ProjectSplitterColumn.Width = new(layout.ProjectVisible ? 4 : 0);
        BottomPanel.Visibility = layout.BottomVisible ? Visibility.Visible : Visibility.Collapsed;
        BottomRow.Height = new(layout.BottomVisible ? layout.BottomHeight : 0);
    }
}
