namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using StudioX.Application;

public partial class MainWindow
{
    private SourceDocument? activeDocument => activeEditor?.Source;
    private EditorSettings editorSettings = new();
    private EditorGlowLayer? editorGlow;
    private BracketPairColorizer? bracketColors;
    public Task OpenFromCommandLineAsync(string directory, params string[] relativePaths) => RunAsync(async token =>
    {
        await OpenProjectAsync(Path.GetFullPath(directory), token);
        if (!string.Equals(projectDirectory, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        foreach (var relativePath in relativePaths)
        {
            await OpenSourceAsync(relativePath, token);
        }
    });

    private void InitializeEditor()
    {
        SourceEditor.HighlightingFailed += ReportHighlightingFailure;
        editorGlow = new EditorGlowLayer(SourceEditor.TextArea.TextView);
        bracketColors = new BracketPairColorizer(SourceEditor, ex => Log("括号配色不可用：" + ex));
        Closed += (_, _) => bracketColors.Dispose();
        SourceEditor.Options.IndentationSize = 4;
        SourceEditor.Options.ConvertTabsToSpaces = true;
        SourceEditor.Options.HighlightCurrentLine = true;
        SourceEditor.Options.EnableHyperlinks = false;
        SourceEditor.Options.EnableEmailHyperlinks = false;
        SourceEditor.TextArea.SelectionCornerRadius = 2;
        foreach (var margin in SourceEditor.TextArea.LeftMargins.OfType<LineNumberMargin>())
        {
            margin.Margin = new Thickness(2, 0, 10, 0);
        }
        SourceEditor.TextArea.Caret.PositionChanged += (_, _) => UpdateEditorPosition();
        InitializeFindReplace();
        InitializeDiagnostics();
        InitializeEditingActions();
        InitializeCodeAssistance();
        InitializeCodeNavigation();
        InitializeDocumentTabs();
        InitializeOutline();
        InitializeExplorer();
        InitializeDebugger();
        InitializeWorkspaceEditing();
        InitializePeripheralDevelopment();
        InitializeSourceRegistration();
        InitializeLiveDiagnostics();
        InitializeEditorRecovery();
        ApplyEditorSettings(editorSettings);
    }
    private void ApplyEditorSettings(EditorSettings settings)
    {
        EditorSettingsService.Validate(settings);
        editorSettings = settings;
        SourceEditor.FontFamily = new FontFamily(settings.FontFamily + ", Consolas");
        SourceEditor.FontSize = settings.FontSize;
        ApplyEditorTheme();
    }
    private void ApplyEditorTheme()
    {
        var background = (Color)ColorConverter.ConvertFromString(currentTheme.Colors["Background"]);
        var dark = (background.R * .299 + background.G * .587 + background.B * .114) < 140;
        System.Windows.Application.Current.Resources["DebugChanged"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#F3C66E" : "#946600"));
        var view = SourceEditor.TextArea.TextView;
        SourceEditor.LineNumbersForeground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#727680" : "#92959C"));
        view.CurrentLineBackground = new SolidColorBrush(Color.FromArgb(dark ? (byte)20 : (byte)12, 128, 153, 190));
        view.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        SourceEditor.TextArea.SelectionBrush = new SolidColorBrush(Color.FromArgb(100, 66, 110, 180));
        SourceEditor.TextArea.SelectionForeground = null;
        view.Effect = null;
        editorGlow?.Configure(dark && editorSettings.SoftGlow, editorSettings.GlowStrength, editorSettings.FontSize);
        var language = CodeLanguage.ForFile(activeDocument?.RelativePath ?? "");
        try
        {
            SourceEditor.SyntaxHighlighting = CodeLanguage.Get(language, dark);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 规则加载失败同样只关闭着色；文件文本和编辑缓冲区必须继续可用。
            SourceEditor.SyntaxHighlighting = null;
            ReportHighlightingFailure(language, ex);
        }
        bracketColors?.Configure(CodeLanguage.ForFile(activeDocument?.RelativePath ?? ""), dark);
        RefreshInactiveCode();
        InvalidateFileIcons(ProjectTree);
        foreach (var session in editorDocuments)
        {
            if (session.Tab.Header is DependencyObject header)
            {
                InvalidateFileIcons(header);
            }
        }
    }
    private void ReportHighlightingFailure(string language, Exception error)
    {
        var file = activeDocument?.RelativePath ?? "未命名文档";
        Log($"语法高亮失败：{file} ({language})\n{error}");
        Status.Text = $"{file} 的语法高亮不可用，已切换为纯文本；详情见日志。";
    }
    private static void InvalidateFileIcons(DependencyObject parent)
    {
        if (parent is FileIcon icon)
        {
            icon.InvalidateVisual();
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            InvalidateFileIcons(VisualTreeHelper.GetChild(parent, index));
        }
    }
    private void ShowSource(SourceDocument document)
    {
        // 明确加载一个新快照；正常切换标签走 ActivateEditor，不替换内存文本或撤销栈。
        var existing = FindEditor(document.RelativePath);
        if (existing is not null)
        {
            RemoveEditor(existing);
        }
        var session = AddEditor(document);
        ShowDocument(session.Tab);
    }
    private async Task OpenSourceAsync(string relativePath, CancellationToken token)
    {
        if (FindEditor(relativePath) is { } existing)
        {
            ShowDocument(existing.Tab);
            SourceEditor.Focus();
            return;
        }
        var document = await services.Files.ReadAsync(RequireProject(), relativePath, token);
        ShowSource(document);
        SourceEditor.Focus();
    }
    private void SourceEditor_TextChanged(object? sender, EventArgs e)
    {
        QueueLiveDiagnostics();
        CancelCodeRequest();
        CancelSymbolRequests();
        DebugSourceChanged();
        QueueOutlineRefresh();
    }
    private void UpdateEditorPosition()
    {
        if (EditorPosition is not null)
        {
            EditorPosition.Text = $"行 {SourceEditor.TextArea.Caret.Line}，列 {SourceEditor.TextArea.Caret.Column}";
        }
    }
    private async Task PopulateProjectTreeAsync(string name, bool expandSource = true, CancellationToken token = default)
    {
        CancelProjectTreeLoading();
        projectTreeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        loadedProjectDirectories.Clear();
        loadingProjectDirectories.Clear();
        ProjectTree.Items.Clear();
        var root = new TreeViewItem { Header = FileLabel(name, true), Tag = new ProjectEntry(name, "", true, false) };
        ProjectTree.Items.Add(root);
        root.IsExpanded = true;
        ProjectTree.Visibility = Visibility.Visible;
        EmptyProject.Visibility = Visibility.Collapsed;
        await LoadChildrenAsync(root, token);
        foreach (var child in root.Items.OfType<TreeViewItem>())
        {
            if (expandSource && child.Tag is ProjectEntry { RelativePath: "src" })
            {
                child.IsExpanded = true;
                await LoadChildrenAsync(child, token);
            }
        }
        if (expandSource && services.Files.FileExists(RequireProject(), "Core/Src/main.c") && await FindProjectNodeAsync("Core/Src", token) is { } coreSource)
        {
            coreSource.IsExpanded = true;
            await LoadChildrenAsync(coreSource, token);
        }
    }
    private CancellationTokenSource? projectTreeCancellation;
    private readonly HashSet<TreeViewItem> loadedProjectDirectories = [];
    private readonly Dictionary<TreeViewItem, Task> loadingProjectDirectories = [];
    private void CancelProjectTreeLoading()
    {
        projectTreeCancellation?.Cancel();
        projectTreeCancellation?.Dispose();
        projectTreeCancellation = null;
    }
    private Task LoadChildrenAsync(TreeViewItem parent, CancellationToken token = default)
    {
        if (loadedProjectDirectories.Contains(parent))
        {
            return Task.CompletedTask;
        }
        if (!loadingProjectDirectories.TryGetValue(parent, out var loading))
        {
            loading = LoadChildrenCoreAsync(parent, token);
            loadingProjectDirectories[parent] = loading;
        }
        return loading.WaitAsync(token);
    }
    private async Task LoadChildrenCoreAsync(TreeViewItem parent, CancellationToken token)
    {
        if (parent.Tag is not ProjectEntry entry)
        {
            return;
        }
        if (projectTreeCancellation is null || projectDirectory is not { } directory)
        {
            return;
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(projectTreeCancellation.Token, token);
        projectChangeSession?.TrackPath(entry.RelativePath);
        var stop = lifetime.Token;
        parent.Items.Clear();
        var placeholder = new TreeViewItem { Header = "正在读取…", IsEnabled = false };
        parent.Items.Add(placeholder);
        try
        {
            await Task.Yield();
            var items = await services.Files.ListAsync(directory, entry.RelativePath, stop);
            stop.ThrowIfCancellationRequested();
            parent.Items.Clear();
            var batch = System.Diagnostics.Stopwatch.StartNew();
            foreach (var item in items)
            {
                stop.ThrowIfCancellationRequested();
                var node = CreateProjectNode(item);
                parent.Items.Add(node);
                // 大目录分批挂入节点，让输入和渲染在批次间得到调度；折叠后保留已加载的节点。
                if (batch.ElapsedMilliseconds >= 8)
                {
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background, stop);
                    batch.Restart();
                }
            }
            loadedProjectDirectories.Add(parent);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            parent.Items.Clear();
            parent.Items.Add(new TreeViewItem { Header = "读取已取消（F5 重试）", IsEnabled = false });
            throw;
        }
        catch (Exception ex)
        {
            parent.Items.Clear();
            parent.Items.Add(new TreeViewItem { Header = "无法读取目录（F5 重试）", IsEnabled = false, ToolTip = ex.Message });
            Status.Text = ex.Message;
            Log(ex.ToString());
        }
        finally { loadingProjectDirectories.Remove(parent); }
        SetFolderIcon(parent, parent.IsExpanded);
    }
    private async void Directory_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem node || e.OriginalSource != node)
        {
            return;
        }
        e.Handled = true;
        SetFolderIcon(node, true);
        try
        {
            await LoadChildrenAsync(node);
        }
        catch (OperationCanceledException) { }
    }
    private static void SetFolderIcon(TreeViewItem node, bool expanded)
    {
        if (node.Header is StackPanel row && row.Children[0] is FileIcon icon)
        {
            icon.IsExpanded = expanded;
            icon.InvalidateVisual();
        }
    }
    private static StackPanel FileLabel(string name, bool directory, string suffix = "")
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new FileIcon { FileName = name, IsDirectory = directory, Width = 19, Height = 19, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = name + suffix, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }
    private async void ProjectTree_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProjectTree.SelectedItem is TreeViewItem { Tag: ProjectEntry { IsDirectory: false, IsLink: false } entry })
        {
            e.Handled = true;
            await RunAsync(token => OpenSourceAsync(entry.RelativePath, token));
        }
    }
    private async void ProjectTree_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5 && projectDirectory is not null)
        {
            e.Handled = true;
            await RunAsync(async token => { await ResynchronizeProjectFilesAsync(token); });
        }
        else if (e.Key == Key.Enter && ProjectTree.SelectedItem is TreeViewItem { Tag: ProjectEntry { IsLink: false } entry })
        {
            e.Handled = true;
            await RunAsync(token => OpenExplorerEntryAsync(entry, token));
        }
    }
}
