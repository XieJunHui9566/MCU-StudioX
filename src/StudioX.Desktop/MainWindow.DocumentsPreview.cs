namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>编辑和保存检查仅使用输出目录内的工程副本；用户工程只读。</summary>
    public async Task RenderDocumentsPreviewAsync(string directory, string project)
    {
        var fixture = Path.Combine(directory, "fixture");
        foreach (var path in new[] { ".studiox/project.json", "device/manifest.json", "src/main.c", "CMakeLists.txt", "device/sdk/include/system.h" })
        {
            var output = Path.Combine(fixture, path); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllBytesAsync(output, await File.ReadAllBytesAsync(Path.Combine(project, path)));
        }
        var mainPath = Path.Combine(fixture, "src/main.c");
        await File.AppendAllTextAsync(mainPath, "\n" + string.Join('\n', Enumerable.Range(1, 130).Select(n => $"// scroll fixture {n}: " + new string('x', 180))) + "\n");
        await OpenProjectAsync(fixture, CancellationToken.None);
        var main = activeEditor!;
        var baseline = main.Buffer.Text;
        var headerPath = "device/sdk/include/system.h";
        var headerDisk = await File.ReadAllTextAsync(Path.Combine(fixture, headerPath));
        async Task Layout()
        {
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        }
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        try
        {
            await OpenSourceAsync(headerPath, CancellationToken.None); var header = activeEditor!;
            await OpenSourceAsync("CMakeLists.txt", CancellationToken.None); var cmake = activeEditor!;
            await OpenSourceAsync("SRC/MAIN.C", CancellationToken.None);
            Check(editorDocuments.Count == 3 && activeEditor == main, "同一文件不应重复开标签。");
            SourceEditor.Document.Insert(0, "// unsaved main\n");
            SourceEditor.Select(SourceEditor.Document.GetLineByNumber(85).Offset + 4, 9);
            SourceEditor.CaretOffset = SourceEditor.SelectionStart + SourceEditor.SelectionLength;
            await Layout(); SourceEditor.ScrollToVerticalOffset(920); SourceEditor.ScrollToHorizontalOffset(80); await Layout();
            var caret = SourceEditor.CaretOffset; var start = SourceEditor.SelectionStart; var length = SourceEditor.SelectionLength;
            var vertical = SourceEditor.VerticalOffset; var horizontal = SourceEditor.HorizontalOffset;
            await OpenSourceAsync(headerPath, CancellationToken.None);
            SourceEditor.Document.Insert(0, "// unsaved header\n");
            SourceEditor.Undo(); Check(SourceEditor.Text == headerDisk && main.IsDirty, "撤销历史跨文件污染。");
            SourceEditor.Redo();
            await OpenSourceAsync("src/main.c", CancellationToken.None); await Layout();
            Check(ReferenceEquals(SourceEditor.Document, main.Buffer), "切换后应复用文件文档。");
            Check(SourceEditor.CaretOffset == caret && SourceEditor.SelectionStart == start && SourceEditor.SelectionLength == length, "光标或选区没有恢复。");
            Check(Math.Abs(SourceEditor.VerticalOffset - vertical) < 1 && Math.Abs(SourceEditor.HorizontalOffset - horizontal) < 1, $"滚动位置未恢复：{vertical}/{horizontal} -> {SourceEditor.VerticalOffset}/{SourceEditor.HorizontalOffset}");
            SourceEditor.Undo(); Check(SourceEditor.Text == baseline && header.IsDirty, "主文件撤销被切换清空。"); SourceEditor.Redo();
            Check(main.Label.Text.EndsWith('•') && header.Label.Text.EndsWith('•'), "未保存标记缺失。");

            var saving = SaveEditorAsync(fixture, main, CancellationToken.None);
            ShowDocument(header.Tab); await saving;
            Check((await File.ReadAllTextAsync(mainPath)).StartsWith("// unsaved main", StringComparison.Ordinal) && await File.ReadAllTextAsync(Path.Combine(fixture, headerPath)) == headerDisk, "切换时保存写错文件。");
            await CloseWorkspaceTabAsync(header.Tab, _ => MessageBoxResult.Cancel);
            Check(editorDocuments.Contains(header) && header.IsDirty, "取消关闭丢失了文档。");
            await CloseWorkspaceTabAsync(header.Tab, _ => MessageBoxResult.No);
            Check(!editorDocuments.Contains(header) && await File.ReadAllTextAsync(Path.Combine(fixture, headerPath)) == headerDisk, "不保存关闭写入了磁盘。");
            ShowDocument(main.Tab);
            var close = ((StackPanel)cmake.Tab.Header).Children.OfType<Button>().Single();
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await pendingOperation;
            Check(!editorDocuments.Contains(cmake) && WorkspaceTabs.SelectedItem == main.Tab, "后台标签 X 关闭了错误文件。");
            await OpenSourceAsync(headerPath, CancellationToken.None); header = activeEditor!;
            Check(header.Buffer.Text == headerDisk, "关闭后重开没有读取磁盘。");
            await OpenSourceAsync("CMakeLists.txt", CancellationToken.None); cmake = activeEditor!;
            main.Buffer.Insert(0, "// all main\n"); cmake.Buffer.Insert(0, "# all cmake\n");
            var decisions = 0;
            var accepted = await ConfirmDocumentsAsync(_ => ++decisions == 1 ? MessageBoxResult.No : MessageBoxResult.Cancel);
            Check(!accepted && main.IsDirty && cmake.IsDirty && editorDocuments.Count == 3, "批量关闭取消后应保留所有未保存副本。");
            await SaveAllSourcesAsync(fixture, CancellationToken.None);
            Check(editorDocuments.All(session => !session.IsDirty) && (await File.ReadAllTextAsync(Path.Combine(fixture, "CMakeLists.txt"))).StartsWith("# all cmake", StringComparison.Ordinal), "全部保存未处理后台文件。");
            header.Buffer.Insert(0, "// internal edit\n");
            await File.WriteAllTextAsync(Path.Combine(fixture, headerPath), "// external edit\n" + headerDisk);
            try { await CloseWorkspaceTabAsync(header.Tab, _ => MessageBoxResult.Yes); throw new InvalidOperationException("未拒绝外部修改冲突。"); }
            catch (StudioXException ex) when (ex.Code == "EDITOR_FILE_CHANGED") { }
            Check(editorDocuments.Contains(header) && header.IsDirty, "保存失败后关闭了标签。");
            await CloseWorkspaceTabAsync(header.Tab, _ => MessageBoxResult.No);
            cmake.Buffer.Insert(0, "# save on close\n");
            await CloseWorkspaceTabAsync(cmake.Tab, _ => MessageBoxResult.Yes);
            Check(!editorDocuments.Contains(cmake) && (await File.ReadAllTextAsync(Path.Combine(fixture, "CMakeLists.txt"))).StartsWith("# save on close", StringComparison.Ordinal), "关闭时保存没有成功。");

            Directory.CreateDirectory(Path.Combine(fixture, "other"));
            await File.WriteAllTextAsync(Path.Combine(fixture, "other/main.c"), "int other(void) { return 1; }\n");
            await OpenSourceAsync("other/main.c", CancellationToken.None); var sameName = activeEditor!;
            Check(main.Label.Text.Contains("src", StringComparison.Ordinal) && sameName.Label.Text.Contains("other", StringComparison.Ordinal), "同名文件缺少目录区分。");
            await CloseWorkspaceTabAsync(sameName.Tab);
            Check(main.Label.Text == "main.c", "同名标签关闭后标题未恢复。");
            await OpenSourceAsync(headerPath, CancellationToken.None);
            await OpenSourceAsync("CMakeLists.txt", CancellationToken.None);
            ShowDocument(ExtensionsTab); await CloseWorkspaceTabAsync(ExtensionsTab); Check(ExtensionsTab.Visibility == Visibility.Collapsed, "工具标签未关闭。");
            ShowDocument(ExtensionsTab); ShowDocument(main.Tab); CycleEditor(false); Check(activeEditor!.Source.RelativePath == headerPath, "向前切换失败。");
            CycleEditor(true); Check(activeEditor == main, "向后切换失败。");
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
            {
                ApplyTheme(theme); SourceEditor.ScrollToHome(); SourceEditor.CaretOffset = 0; await Layout();
                Render(this, Path.Combine(directory, "documents-" + theme.Id + ".png"));
            }
            ApplyTheme(ThemeService.Dark);
            for (var index = 0; index < 14; index++) ShowSource(main.Source with { RelativePath = $"preview/peripheral_driver_{index:D2}.c", Text = "// in-memory overflow preview\n" });
            await Layout();
            var scroll = (ScrollViewer)WorkspaceTabs.Template.FindName("DocumentTabScroll", WorkspaceTabs);
            Check(scroll.ScrollableWidth > 0 && scroll.HorizontalOffset > 0, "大量标签没有滚动到选中项。");
            Render(this, Path.Combine(directory, "documents-overflow.png"));
            foreach (var session in editorDocuments.ToArray()) await CloseWorkspaceTabAsync(session.Tab);
            Check(editorDocuments.Count == 0 && activeEditor is null && WorkspaceTabs.SelectedItem == WelcomeTab, "关闭最后文件后未回到欢迎页。");
            await CloseWorkspaceTabAsync(WelcomeTab); Check(WelcomeTab.Visibility == Visibility.Collapsed, "欢迎页不能关闭。"); ShowDocument(WelcomeTab);
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: multi-file buffers/undo/dirty labels, caret/selection/scroll, duplicate paths and basenames, active/background X close, cancel/discard/save/conflict, bulk-close cancellation, save-all, switch during save, keyboard cycling, utility tabs, overflow and dark/light rendering. Only fixture copies were written.\n");
        }
        finally { ClearEditorDocuments(); ShowDocument(WelcomeTab); }
    }
}
