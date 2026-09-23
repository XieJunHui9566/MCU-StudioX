namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Search;
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
        if (!string.Equals(projectDirectory, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase)) return;
        foreach (var relativePath in relativePaths) await OpenSourceAsync(relativePath, token);
    });

    private void InitializeEditor()
    {
        editorGlow = new EditorGlowLayer(SourceEditor.TextArea.TextView);
        bracketColors = new BracketPairColorizer(SourceEditor, ex => Log("括号配色不可用：" + ex));
        Closed += (_, _) => bracketColors.Dispose();
        SourceEditor.Options.IndentationSize = 4;
        SourceEditor.Options.ConvertTabsToSpaces = true;
        SourceEditor.Options.HighlightCurrentLine = true;
        SourceEditor.Options.EnableHyperlinks = false;
        SourceEditor.Options.EnableEmailHyperlinks = false;
        SourceEditor.TextArea.SelectionCornerRadius = 2;
        foreach (var margin in SourceEditor.TextArea.LeftMargins.OfType<LineNumberMargin>()) margin.Margin = new Thickness(2, 0, 10, 0);
        SourceEditor.TextArea.Caret.PositionChanged += (_, _) => UpdateEditorPosition();
        SearchPanel.Install(SourceEditor);
        InitializeDiagnostics();
        InitializeEditingActions();
        InitializeCodeAssistance();
        InitializeCodeNavigation();
        InitializeDocumentTabs();
        InitializeOutline();
        InitializeExplorer();
        InitializeDebugger();
        ApplyEditorSettings(editorSettings);
    }
    private void ApplyEditorSettings(EditorSettings settings)
    {
        EditorSettingsService.Validate(settings); editorSettings = settings;
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
        SourceEditor.SyntaxHighlighting = CodeLanguage.Get(CodeLanguage.ForFile(activeDocument?.RelativePath ?? ""), dark);
        bracketColors?.Configure(CodeLanguage.ForFile(activeDocument?.RelativePath ?? ""), dark);
        InvalidateFileIcons(ProjectTree);
        foreach (var session in editorDocuments)
            if (session.Tab.Header is DependencyObject header) InvalidateFileIcons(header);
    }
    private static void InvalidateFileIcons(DependencyObject parent)
    {
        if (parent is FileIcon icon) icon.InvalidateVisual();
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++) InvalidateFileIcons(VisualTreeHelper.GetChild(parent, index));
    }
    private void ShowSource(SourceDocument document)
    {
        // 明确加载一个新快照；正常切换标签走 ActivateEditor，不替换内存文本或撤销栈。
        var existing = FindEditor(document.RelativePath);
        if (existing is not null) RemoveEditor(existing);
        var session = AddEditor(document);
        ShowDocument(session.Tab);
    }
    private async Task OpenSourceAsync(string relativePath, CancellationToken token)
    {
        if (FindEditor(relativePath) is { } existing) { ShowDocument(existing.Tab); SourceEditor.Focus(); return; }
        var document = await services.Files.ReadAsync(RequireProject(), relativePath, token);
        ShowSource(document); SourceEditor.Focus();
    }
    private void SourceEditor_TextChanged(object? sender, EventArgs e) { CancelCodeRequest(); CancelSymbolRequests(); DebugSourceChanged(); QueueOutlineRefresh(); }
    private void UpdateEditorPosition()
    {
        if (EditorPosition is not null) EditorPosition.Text = $"行 {SourceEditor.TextArea.Caret.Line}，列 {SourceEditor.TextArea.Caret.Column}";
    }
    private void PopulateProjectTree(string name, bool expandSource = true)
    {
        ProjectTree.Items.Clear();
        var root = new TreeViewItem { Header = FileLabel(name, true), Tag = new ProjectEntry(name, "", true, false) };
        LoadChildren(root); ProjectTree.Items.Add(root); root.IsExpanded = true;
        foreach (var child in root.Items.OfType<TreeViewItem>())
            if (expandSource && child.Tag is ProjectEntry { RelativePath: "src" }) child.IsExpanded = true;
        if (expandSource && services.Files.FileExists(RequireProject(), "Core/Src/main.c") && FindProjectNode("Core/Src") is { } coreSource)
            coreSource.IsExpanded = true;
        ProjectTree.Visibility = Visibility.Visible; EmptyProject.Visibility = Visibility.Collapsed;
    }
    private void LoadChildren(TreeViewItem parent)
    {
        if (parent.Tag is not ProjectEntry entry) return;
        parent.Items.Clear();
        try
        {
            foreach (var item in services.Files.List(RequireProject(), entry.RelativePath))
            {
                var deviceSupport = item.IsDirectory && item.RelativePath == "device";
                var node = new TreeViewItem { Header = FileLabel(item.Name, item.IsDirectory, deviceSupport ? " · 器件支持" : ""), Tag = item, ToolTip = deviceSupport ? "厂商 SDK、寄存器定义、启动文件及内部构建配置；应用代码在 src 中维护。" : item.RelativePath, IsEnabled = !item.IsLink };
                if (item.IsLink) node.ToolTip = item.RelativePath + "（链接目录或文件暂不展开）";
                if (item.IsDirectory && !item.IsLink)
                {
                    node.Items.Add(new TreeViewItem { Header = "正在读取…", IsEnabled = false });
                    node.Expanded += Directory_Expanded;
                    node.Collapsed += (_, e) => { if (e.OriginalSource == node) SetFolderIcon(node, false); };
                }
                parent.Items.Add(node);
            }
        }
        catch (Exception ex)
        {
            parent.Items.Add(new TreeViewItem { Header = "无法读取目录（F5 重试）", IsEnabled = false, ToolTip = ex.Message });
            Status.Text = ex.Message; Log(ex.ToString());
        }
        SetFolderIcon(parent, true);
    }
    private void Directory_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem node || e.OriginalSource != node) return;
        LoadChildren(node); e.Handled = true;
    }
    private static void SetFolderIcon(TreeViewItem node, bool expanded)
    {
        if (node.Header is StackPanel row && row.Children[0] is FileIcon icon) { icon.IsExpanded = expanded; icon.InvalidateVisual(); }
    }
    private static StackPanel FileLabel(string name, bool directory, string suffix = "")
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new FileIcon { FileName = name, IsDirectory = directory, Width = 19, Height = 19, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = name + suffix, VerticalAlignment = VerticalAlignment.Center }); return row;
    }
    private async void ProjectTree_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProjectTree.SelectedItem is TreeViewItem { Tag: ProjectEntry { IsDirectory: false, IsLink: false } entry })
        { e.Handled = true; await RunAsync(token => OpenSourceAsync(entry.RelativePath, token)); }
    }
    private async void ProjectTree_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5 && projectDirectory is not null) { RefreshProjectTree(); e.Handled = true; }
        else if (e.Key == Key.Enter && ProjectTree.SelectedItem is TreeViewItem { Tag: ProjectEntry { IsLink: false } entry })
        { e.Handled = true; await RunAsync(token => OpenExplorerEntryAsync(entry, token)); }
    }
}
