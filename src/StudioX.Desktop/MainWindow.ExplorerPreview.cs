namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StudioX.Application;

public partial class MainWindow
{
    /// <summary>文件操作仅发生在隔离工程副本，不改写用户源码或系统剪贴板。</summary>
    public async Task RenderExplorerPreviewAsync(string directory, string project)
    {
        var fixture = Path.Combine(directory, "fixture");
        foreach (var path in new[] { ".studiox/project.json", "device/manifest.json", "src/main.c", "CMakeLists.txt", "device/sdk/include/system.h" })
        {
            var output = Path.Combine(fixture, path); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllBytesAsync(output, await File.ReadAllBytesAsync(Path.Combine(project, path)));
        }
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        async Task Layout() { UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render); }
        await OpenProjectAsync(fixture, CancellationToken.None);
        try
        {
            var main = activeEditor!; var original = main.Buffer.Text;
            main.Buffer.Insert(0, "// 未保存内容\n");
            SourceEditor.CaretOffset = 10;
            var origin = CaptureNavigationPoint()!; navigationBack.Add(origin);
            var treeSource = FindProjectNode("src")!; treeSource.IsSelected = true;
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
            {
                ApplyTheme(theme); await Layout();
                explorerMenuEntry = (ProjectEntry)treeSource.Tag; explorerMouseContext = true;
                var menu = explorerMenu!; menu.PlacementTarget = treeSource; menu.IsOpen = true; menu.UpdateLayout(); await Layout();
                Check(menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "重命名…")).IsEnabled, "src 应可重命名");
                Render(menu, Path.Combine(directory, "explorer-menu-" + theme.Id + ".png"));
                menu.IsOpen = false;
            }
            ApplyTheme(ThemeService.Dark);
            await RenameExplorerEntryCoreAsync(new("src", "src", true, false), "Sources", CancellationToken.None);
            Check(main.Source.RelativePath == "Sources/main.c" && SourceEditor.Document == main.Buffer && main.IsDirty && SourceEditor.CaretOffset == 10, "文件夹改名丢失标签、光标或未保存内容");
            Check(navigationBack.Single().Path == "Sources/main.c", "历史位置未同步改名");
            SourceEditor.Undo(); Check(SourceEditor.Text == original, "重命名破坏撤销历史"); SourceEditor.Redo();
            await SaveEditorAsync(fixture, main, CancellationToken.None);
            Check(await File.ReadAllTextAsync(Path.Combine(fixture, "Sources/main.c")) == main.Buffer.Text && !File.Exists(Path.Combine(fixture, "src/main.c")), "重命名后保存写错路径");
            await RenameExplorerEntryCoreAsync(new("main.c", "Sources/main.c", false, false), "main.h", CancellationToken.None);
            Check(main.Label.Text.StartsWith("main.h", StringComparison.Ordinal) && EditorBreadcrumb.Text.Contains("main.h", StringComparison.Ordinal), "文件改名未同步标签/路径");
            await CreateExplorerEntryCoreAsync("Sources", "user.c", false, CancellationToken.None);
            Check(activeDocument?.RelativePath == "Sources/user.c" && FindProjectNode("Sources/user.c")?.IsSelected == true, "新建文件未打开并选中");
            var copies = await services.Files.CopyEntriesAsync(fixture, "Sources", [Path.Combine(fixture, "Sources/user.c")]);
            RefreshProjectTree(copies.Single());
            Check(FindProjectNode("Sources/user - 副本.c")?.IsSelected == true, "粘贴未定位副本");
            await CreateExplorerEntryCoreAsync("Sources", "include", true, CancellationToken.None);
            Check(Directory.Exists(Path.Combine(fixture, "Sources/include")), "新建文件夹失败");
            await Layout(); Render(this, Path.Combine(directory, "explorer-files.png"));
            var prompt = new EntryNameWindow("重命名", "名称：Sources/main.h", "main.h", false) { Owner = this, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000 };
            prompt.Show(); prompt.UpdateLayout(); await Layout(); Render(prompt, Path.Combine(directory, "rename-dialog.png")); prompt.Close();
            ClearEditorDocuments(); projectDirectory = null;
            await OpenProjectAsync(fixture, CancellationToken.None);
            Check(projectDirectory == fixture && ProjectTree.Items.Count > 0, "主文件/目录改名后工程不能重新打开");
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: themed context menu, file/folder rename with dirty buffers/undo/caret/history, save at renamed path, create/open/select, copy collision selection, rename dialog and reopen without src/main.c. Only fixture files modified; clipboard untouched.\n");
        }
        finally { explorerMenu!.IsOpen = false; ClearEditorDocuments(); }
    }
}
