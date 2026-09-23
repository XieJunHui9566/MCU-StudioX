namespace StudioX.Desktop;

using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StudioX.Application;
using StudioX.Foundation;

public partial class MainWindow
{
    private ContextMenu? explorerMenu;
    private ProjectEntry? explorerMenuEntry;
    private bool explorerMouseContext;
    private ProjectEntry? SelectedProjectEntry => (ProjectTree.SelectedItem as TreeViewItem)?.Tag as ProjectEntry;
    private ProjectEntry? RootProjectEntry => (ProjectTree.Items.Cast<object>().FirstOrDefault() as TreeViewItem)?.Tag as ProjectEntry;
    private static string DestinationFolder(ProjectEntry entry) => entry.IsDirectory ? entry.RelativePath : ProjectFileService.ParentDirectory(entry.RelativePath);

    private void InitializeExplorer()
    {
        var menu = new ContextMenu(); SetMenuColors(menu); explorerMenu = menu;
        MenuItem Add(string label, string gesture, Func<ProjectEntry, CancellationToken, Task> action)
        {
            var item = new MenuItem { Header = label, InputGestureText = gesture };
            item.Click += async (_, _) => { if (explorerMenuEntry is { } entry) await RunAsync(token => action(entry, token)); };
            menu.Items.Add(item); return item;
        }
        var open = Add("打开", "Enter", OpenExplorerEntryAsync);
        menu.Items.Add(new Separator());
        var file = Add("新建文件…", "", (entry, token) => CreateExplorerEntryAsync(entry, false, token));
        var folder = Add("新建文件夹…", "", (entry, token) => CreateExplorerEntryAsync(entry, true, token));
        menu.Items.Add(new Separator());
        var copy = Add("复制", "Ctrl+C", (entry, _) => { CopyExplorerEntry(entry); return Task.CompletedTask; });
        var paste = Add("粘贴", "Ctrl+V", PasteExplorerEntriesAsync);
        var rename = Add("重命名…", "F2", RenameExplorerEntryAsync);
        menu.Items.Add(new Separator());
        Add("复制完整路径", "", (entry, _) => { Clipboard.SetText(services.Files.GetEntryPath(RequireProject(), entry.RelativePath)); return Task.CompletedTask; });
        Add("复制相对路径", "", (entry, _) => { Clipboard.SetText(entry.RelativePath.Length == 0 ? "." : entry.RelativePath); return Task.CompletedTask; });
        Add("在资源管理器中显示", "", (entry, _) => { services.Files.ShowInExplorer(RequireProject(), entry.RelativePath); return Task.CompletedTask; });
        menu.Items.Add(new Separator());
        Add("刷新", "F5", (entry, _) => { RefreshProjectTree(entry.RelativePath); return Task.CompletedTask; });
        menu.Opened += (_, _) =>
        {
            if (!explorerMouseContext) explorerMenuEntry = SelectedProjectEntry ?? RootProjectEntry;
            var entry = explorerMenuEntry;
            var available = entry is { IsLink: false } && projectDirectory is not null && pendingOperation.IsCompleted && !closing;
            foreach (var item in menu.Items.OfType<MenuItem>()) item.IsEnabled = available;
            if (!available || entry is null) return;
            var target = DestinationFolder(entry);
            file.IsEnabled = folder.IsEnabled = !services.Debugger.IsActive && ProjectFileService.CanCreateIn(target);
            rename.IsEnabled = !services.Debugger.IsActive && ProjectFileService.CanRenameEntry(entry.RelativePath);
            copy.IsEnabled = entry.RelativePath.Length > 0;
            try { paste.IsEnabled = !services.Debugger.IsActive && ProjectFileService.CanCreateIn(target) && Clipboard.ContainsFileDropList(); }
            catch (System.Runtime.InteropServices.ExternalException) { paste.IsEnabled = false; }
            open.Header = entry.IsDirectory ? "展开 / 折叠" : "打开";
        };
        menu.Closed += (_, _) => explorerMouseContext = false;
        ProjectTree.ContextMenu = menu;
        ProjectTree.PreviewKeyDown += Explorer_PreviewKeyDown;
        ProjectTree.PreviewMouseRightButtonDown += (_, e) =>
        {
            var node = FindTreeNode(e.OriginalSource as DependencyObject);
            if (node?.Tag is ProjectEntry entry) { node.IsSelected = true; node.Focus(); explorerMenuEntry = entry; }
            else { explorerMenuEntry = RootProjectEntry; ProjectTree.Focus(); }
            explorerMouseContext = true;
        };
    }
    private async void Explorer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var entry = SelectedProjectEntry ?? RootProjectEntry;
        if (entry is null || entry.IsLink) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C && entry.RelativePath.Length > 0)
        { e.Handled = true; await RunAsync(_ => { CopyExplorerEntry(entry); return Task.CompletedTask; }); }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V && ProjectFileService.CanCreateIn(DestinationFolder(entry)))
        { e.Handled = true; await RunAsync(token => PasteExplorerEntriesAsync(entry, token)); }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.F2)
        { e.Handled = true; await RunAsync(token => RenameExplorerEntryAsync(entry, token)); }
    }
    private static TreeViewItem? FindTreeNode(DependencyObject? element)
    {
        while (element is not null && element is not TreeViewItem)
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return element as TreeViewItem;
    }
    private Task OpenExplorerEntryAsync(ProjectEntry entry, CancellationToken token)
    {
        if (!entry.IsDirectory) return OpenSourceAsync(entry.RelativePath, token);
        if (FindProjectNode(entry.RelativePath) is { } node) node.IsExpanded = !node.IsExpanded;
        return Task.CompletedTask;
    }
    private void CopyExplorerEntry(ProjectEntry entry)
    {
        var paths = new StringCollection { services.Files.GetEntryPath(RequireProject(), entry.RelativePath) };
        Clipboard.SetFileDropList(paths);
        Status.Text = "已复制磁盘文件：" + entry.Name;
    }
    private async Task CreateExplorerEntryAsync(ProjectEntry entry, bool directory, CancellationToken token)
    {
        EnsureNoActiveDebug();
        var parent = DestinationFolder(entry);
        var logicFolder = currentProjectManifest?.Logic is not null && (parent.Equals("logic", StringComparison.OrdinalIgnoreCase) || parent.StartsWith("logic/", StringComparison.OrdinalIgnoreCase));
        var suggestedName = directory ? "新建文件夹" : logicFolder ? "user_module.v" : "untitled.c";
        var dialog = new EntryNameWindow(directory ? "新建文件夹" : "新建文件", "位置：" + (parent.Length == 0 ? "工程根目录" : parent), suggestedName, directory) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await CreateExplorerEntryCoreAsync(parent, dialog.EntryName, directory, token);
    }
    private async Task CreateExplorerEntryCoreAsync(string parent, string name, bool directory, CancellationToken token)
    {
        var path = await services.Files.CreateEntryAsync(RequireProject(), parent, name, directory, token);
        RefreshProjectTree(path);
        if (!directory) await OpenSourceAsync(path, token);
        await RefreshExplorerLanguageAsync(token);
        var sourceFile = Path.GetExtension(path).ToLowerInvariant() is ".c" or ".cpp" or ".cc" or ".cxx" or ".s" or ".asm";
        var logicFile = !directory && currentProjectManifest?.Logic is not null && Path.GetExtension(path).ToLowerInvariant() is ".v" or ".sv" or ".ve";
        Status.Text = "已新建 " + path + (sourceFile ? " · 源文件需要在 CMakeLists.txt 中添加到目标" : logicFile ? " · 逻辑文件需加入厂商逻辑工程" : "");
    }
    private async Task PasteExplorerEntriesAsync(ProjectEntry entry, CancellationToken token)
    {
        EnsureNoActiveDebug();
        if (!Clipboard.ContainsFileDropList()) return;
        var sources = Clipboard.GetFileDropList().Cast<string>().ToArray();
        var paths = await services.Files.CopyEntriesAsync(RequireProject(), DestinationFolder(entry), sources, token);
        RefreshProjectTree(paths.LastOrDefault());
        await RefreshExplorerLanguageAsync(token);
        Status.Text = $"已粘贴 {paths.Count} 项 · 同名文件保留为副本；C/C++ 源文件需加入 CMake" +
            (currentProjectManifest?.Logic is not null ? "，逻辑源文件需加入厂商逻辑工程" : "");
    }
    private async Task RenameExplorerEntryAsync(ProjectEntry entry, CancellationToken token)
    {
        EnsureNoActiveDebug();
        if (!ProjectFileService.CanRenameEntry(entry.RelativePath)) return;
        var dialog = new EntryNameWindow("重命名", "名称：" + entry.RelativePath, entry.Name, entry.IsDirectory) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.EntryName == entry.Name) return;
        await RenameExplorerEntryCoreAsync(entry, dialog.EntryName, token);
    }
    private async Task RenameExplorerEntryCoreAsync(ProjectEntry entry, string name, CancellationToken token)
    {
        EnsureNoActiveDebug();
        token.ThrowIfCancellationRequested(); CloseCodeAssistance(); CaptureEditorView();
        var previous = entry.RelativePath;
        ProjectFileService.ValidateEntryName(name);
        var parent = ProjectFileService.ParentDirectory(previous);
        var proposed = parent.Length == 0 ? name : parent + "/" + name;
        var mappedPaths = editorDocuments.Select(session => ProjectFileService.ContainsPath(previous, session.Source.RelativePath) ? proposed + session.Source.RelativePath[previous.Length..] : session.Source.RelativePath);
        if (mappedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != editorDocuments.Count)
            throw new StudioXException("EDITOR_PATH_OPEN", "目标路径已有打开的标签，请先处理该标签中的内容。");
        var renamed = services.Files.RenameEntry(RequireProject(), previous, name);
        ClearBuildDiagnostics();
        string Remap(string path) => ProjectFileService.ContainsPath(previous, path) ? renamed + path[previous.Length..] : path;
        foreach (var session in editorDocuments)
        {
            var path = Remap(session.Source.RelativePath);
            if (path == session.Source.RelativePath) continue;
            session.Source = session.Source with { RelativePath = path };
            if (session.Tab.Header is StackPanel { Children.Count: > 0 } row && row.Children[0] is StackPanel { Children.Count: > 0 } label && label.Children[0] is FileIcon icon)
            {
                var replacement = new FileIcon { FileName = path, Width = icon.Width, Height = icon.Height, Margin = icon.Margin, VerticalAlignment = icon.VerticalAlignment };
                label.Children.RemoveAt(0); label.Children.Insert(0, replacement);
            }
        }
        for (var i = 0; i < navigationBack.Count; i++) navigationBack[i] = navigationBack[i] with { Path = Remap(navigationBack[i].Path) };
        for (var i = 0; i < navigationForward.Count; i++) navigationForward[i] = navigationForward[i] with { Path = Remap(navigationForward[i].Path) };
        UpdateEditorHeaders(); RefreshActiveEditorMetadata();
        RefreshProjectTree(renamed, previous);
        await RefreshExplorerLanguageAsync(token);
        Status.Text = "已重命名为 " + renamed + " · 请同步修改 CMake 和 #include 中引用的路径";
    }
    private void RefreshActiveEditorMetadata()
    {
        if (activeDocument is not { } document) return;
        EditorBreadcrumb.Text = document.RelativePath.Replace("/", "  ›  "); EditorBreadcrumb.ToolTip = document.RelativePath;
        EditorLanguage.Text = $"{CodeLanguage.ForFile(document.RelativePath)}    ·    {document.Encoding.WebName.ToUpperInvariant()}    ·    {(SourceEditor.Text.Contains("\r\n") ? "CRLF" : "LF")}" + (document.IsReadOnly ? "    ·    " + (document.ReadOnlyReason ?? "只读") : "");
        ApplyEditorTheme();
    }
    private async Task RefreshExplorerLanguageAsync(CancellationToken token)
    {
        CloseCodeAssistance();
        try { await services.Intelligence.StartAsync(RequireProject(), token); QueueOutlineRefresh(clear: true); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log(ex.ToString()); Log("文件操作已完成，语言服务刷新失败；重新打开工程可重试。"); }
    }
    private TreeViewItem? FindProjectNode(string path)
    {
        if (ProjectTree.Items.Cast<object>().FirstOrDefault() is not TreeViewItem root) return null;
        if (path.Length == 0) return root;
        var node = root; var accumulated = "";
        foreach (var part in path.Split('/'))
        {
            node.IsExpanded = true;
            accumulated = accumulated.Length == 0 ? part : accumulated + "/" + part;
            var child = node.Items.OfType<TreeViewItem>().FirstOrDefault(item => item.Tag is ProjectEntry entry && entry.RelativePath.Equals(accumulated, StringComparison.OrdinalIgnoreCase));
            if (child is null) return null;
            node = child;
        }
        return node;
    }
    private void RefreshProjectTree(string? selected = null, string? renamedFrom = null)
    {
        selected ??= SelectedProjectEntry?.RelativePath ?? "";
        var expanded = new List<string>();
        void Capture(ItemsControl parent)
        {
            foreach (var child in parent.Items.OfType<TreeViewItem>())
                if (child is { IsExpanded: true, Tag: ProjectEntry entry }) { expanded.Add(entry.RelativePath); Capture(child); }
        }
        Capture(ProjectTree);
        PopulateProjectTree(WindowProjectTitle.Text, expandSource: false);
        foreach (var path in expanded.OrderBy(path => path.Length))
        {
            var adjusted = renamedFrom is not null && ProjectFileService.ContainsPath(renamedFrom, path) ? selected + path[renamedFrom.Length..] : path;
            if (FindProjectNode(adjusted) is { } node) node.IsExpanded = true;
        }
        if (FindProjectNode(selected) is { } target) { target.IsSelected = true; target.BringIntoView(); }
    }
}
