namespace StudioX.Desktop;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>只使用工程副本：检查原生滚轮消息与工程关闭，不构建或访问设备。</summary>
    public async Task RenderProjectPreviewAsync(string directory, string project)
    {
        var fixture = Path.Combine(directory, "fixture");
        if (Directory.Exists(fixture)) throw new InvalidOperationException("检查需要新的输出目录。");
        foreach (var path in Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(project, path).Replace('\\', '/');
            if (relative.StartsWith(".build/", StringComparison.Ordinal) || relative.StartsWith(".git/", StringComparison.Ordinal)) continue;
            var target = Path.Combine(fixture, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target);
        }
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        async Task Layout() { UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render); }
        await RunAsync(token => OpenProjectAsync(fixture, token));
        try
        {
            var main = activeEditor ?? throw new InvalidOperationException("主文件未打开。");
            Check(services.Intelligence.IsReady, "语言服务未启动。");
            var text = SourceEditor.Text; var caret = SourceEditor.CaretOffset;
            var font = SourceEditor.FontSize;
            await Layout();
            var lineHeight = SourceEditor.TextArea.TextView.DefaultLineHeight;
            await Wheel(SourceEditor, 360, true);
            Check(SourceEditor.FontSize == font + 3 && SourceEditor.TextArea.TextView.DefaultLineHeight > lineHeight,
                "原生 Ctrl+滚轮没有放大实际文字行高。");
            Render(this, Path.Combine(directory, "wheel-enlarged.png"));
            await Wheel(SourceEditor, -360, true);
            Check(SourceEditor.FontSize == font, "原生 Ctrl+滚轮没有缩小。");
            await Wheel(SourceEditor, 60, true);
            Check(SourceEditor.FontSize == font, "高精度滚轮被提前整格处理。");
            await Wheel(SourceEditor, 60, true);
            Check(SourceEditor.FontSize == font + 1, "高精度滚轮未累积。");
            await Wheel(SourceEditor, -120, true);
            await Wheel(SourceEditor, -120, false);
            Check(SourceEditor.FontSize == font && SourceEditor.Text == text && SourceEditor.CaretOffset == caret && !main.IsDirty,
                "普通滚轮改变字号，或缩放改写文档/光标。");
            await pendingZoomSave;
            Check((await services.EditorSettings.LoadAsync()).FontSize == font, "字号未保存。");
            ShowBottom(0); BuildLog.Text = "滚轮检查\n第二行"; await Layout();
            var logFont = BuildLog.FontSize;
            await Wheel(BuildLog, 120, true);
            Check(BuildLog.FontSize == logFont + 1 && SourceEditor.FontSize == font, "输出缩放影响了代码区。");

            await OpenSourceAsync("CMakeLists.txt", CancellationToken.None); var cmake = activeEditor!;
            main.Buffer.Insert(0, "// unsaved main\n"); cmake.Buffer.Insert(0, "# unsaved cmake\n");
            var mainBefore = main.Buffer.Text; var cmakeBefore = cmake.Buffer.Text;
            await ShowProjectDetailsAsync();
            Check(projectDirectory == fixture && main.IsDirty && cmake.IsDirty && editorDocuments.Count == 2 &&
                services.Intelligence.IsReady && WorkspaceTabs.SelectedItem == PackagesTab &&
                NewProjectPanel.Visibility == Visibility.Collapsed && !NewProjectPanel.IsEnabled &&
                ProjectDetailsPanel.Visibility == Visibility.Visible && CurrentProjectDeviceId.Text == currentProjectManifest!.DeviceId &&
                main.Buffer.Text == mainBefore && cmake.Buffer.Text == cmakeBefore,
                "查看器件与模板改变了工程、未保存文件或语言服务状态。");
            await Layout(); Render(this, Path.Combine(directory, "current-project-details.png"));
            BuildOptimizationPicker.SelectedValue = CompilerOptimization.Og;
            BuildDebugInfoPicker.SelectedValue = CompilerDebugInfo.Full;
            Check(SaveBuildSettingsButton.IsEnabled && BuildSettingsSummary.Text == "-Og -g3", "编译参数修改未启用保存。");
            SaveBuildSettingsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); await pendingOperation;
            Check(await services.Builds.LoadSettingsAsync(fixture) == new ProjectBuildSettings(Optimization: CompilerOptimization.Og, DebugInfo: CompilerDebugInfo.Full) &&
                !SaveBuildSettingsButton.IsEnabled && main.Buffer.Text == mainBefore && cmake.Buffer.Text == cmakeBefore,
                "编译参数没有保存，或改写了编辑器内容。");
            await ShowProjectDetailsAsync();
            Check(SelectedBuildSettings.Optimization == CompilerOptimization.Og, "重开页面丢失已保存参数。");
            Width = 1600; Height = 950;
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
            {
                ApplyTheme(theme); await Layout(); Render(this, Path.Combine(directory, "build-settings-" + theme.Id + ".png"));
            }
            Width = 1120; ApplyTheme(ThemeService.Dark); await Layout();
            BuildSettingsEditor.BringIntoView(); await Layout(); Render(this, Path.Combine(directory, "build-settings-narrow.png"));
            ResetBuildSettings_Click(this, new RoutedEventArgs());
            SaveBuildSettingsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); await pendingOperation;
            Check(await services.Builds.LoadSettingsAsync(fixture) == new ProjectBuildSettings(), "恢复工程默认未保存。");
            await File.WriteAllTextAsync(Path.Combine(directory, "build-settings-result.txt"), "PASS: UI selection, save, reopen, reset, unchanged dirty buffers and dark/light/narrow rendering. Only fixture settings changed.\n");
            ShowDocument(cmake.Tab);
            var count = 0;
            await BeginNewProjectAsync(CancellationToken.None, _ => ++count == 1 ? MessageBoxResult.No : MessageBoxResult.Cancel);
            Check(projectDirectory == fixture && main.IsDirty && cmake.IsDirty && editorDocuments.Count == 2 && services.Intelligence.IsReady && WorkspaceTabs.SelectedItem == cmake.Tab,
                "取消新建/关闭没有完整保留旧工程。");
            var diskMain = Path.Combine(fixture, "src/main.c");
            await File.WriteAllTextAsync(diskMain, "// external change\n" + text);
            try { await CloseProjectAsync(CancellationToken.None, _ => MessageBoxResult.Yes); throw new InvalidOperationException("保存冲突应阻止关闭。"); }
            catch (StudioXException ex) when (ex.Code == "EDITOR_FILE_CHANGED") { }
            Check(projectDirectory == fixture && editorDocuments.Count == 2 && main.IsDirty, "保存失败后丢失工程。");
            await File.WriteAllTextAsync(diskMain, text);
            Check(await CloseProjectAsync(CancellationToken.None, _ => MessageBoxResult.Yes), "保存并关闭失败。");
            ShowDocument(WelcomeTab);
            Check((await File.ReadAllTextAsync(diskMain)).StartsWith("// unsaved main", StringComparison.Ordinal), "关闭时未保存修改。");
            CheckClosed(); await Layout(); Render(this, Path.Combine(directory, "project-closed.png"));
            await RunAsync(token => OpenProjectAsync(fixture, token));
            Check(services.Intelligence.IsReady && BuildButton.IsEnabled, "关闭后重开工程失败。");
            activeEditor!.Buffer.Insert(0, "// discarded\n");
            await BeginNewProjectAsync(CancellationToken.None, _ => MessageBoxResult.No);
            CheckClosed();
            Check(WorkspaceTabs.SelectedItem == PackagesTab && NewProjectPanel.Visibility == Visibility.Visible && NewProjectPanel.IsEnabled &&
                ProjectDetailsPanel.Visibility == Visibility.Collapsed && !(await File.ReadAllTextAsync(diskMain)).Contains("// discarded", StringComparison.Ordinal), "新建入口未清空工程或写入放弃的内容。");
            await Layout(); Render(this, Path.Combine(directory, "new-project-empty.png"));
            await RunAsync(token => OpenProjectAsync(fixture, token));
            CloseProjectMenu.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent)); await pendingOperation;
            CheckClosed(); Check(WorkspaceTabs.SelectedItem == WelcomeTab && NewProjectPanel.IsEnabled && currentProjectManifest is null, "关闭菜单未回到欢迎页或遗留只读配置。");
            await File.WriteAllTextAsync(Path.Combine(directory, "project-details-result.txt"), "PASS: read-only device/template view preserves two dirty buffers, project tree, editor tabs and language server. Explicit new/close restores creation mode.\n");
            await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: HWND Ctrl+wheel enlarges actual rendered line height; down/up, partial deltas, regular wheel, log scope and font persistence. Close project: cancel preserves all buffers, save conflict preserves project, save/discard, release/restart language server, empty tree/tabs/targets, new-project reset and File menu. Only fixture files changed; no build or hardware access.\n");
        }
        finally { ClearEditorDocuments(); await services.Intelligence.StopAsync(); }

        void CheckClosed() => Check(projectDirectory is null && ProjectTree.Items.Count == 0 && ProjectTree.Visibility == Visibility.Collapsed &&
            EmptyProject.Visibility == Visibility.Visible && editorDocuments.Count == 0 && activeEditor is null && SourceEditor.Text.Length == 0 &&
            !services.Intelligence.IsReady && !BuildButton.IsEnabled && !BuildMenu.IsEnabled && !DownloadButton.IsEnabled && !CloseProjectMenu.IsEnabled &&
            WindowProjectTitle.Text == "欢迎" && BuildConfiguration.Text == "无构建目标" && navigationBack.Count == 0 && navigationForward.Count == 0, "旧工程状态未完全清除。");
        async Task Wheel(FrameworkElement target, int delta, bool control)
        {
            await Layout();
            var screen = target.PointToScreen(new Point(Math.Min(80, target.ActualWidth / 2), Math.Min(40, target.ActualHeight / 2)));
            var coordinates = unchecked((nint)(((int)screen.Y & 0xffff) << 16 | ((int)screen.X & 0xffff)));
            var keys = unchecked((nint)((delta & 0xffff) << 16 | (control ? 0x0008 : 0)));
            SendPreviewWheel(new WindowInteropHelper(this).Handle, 0x020A, keys, coordinates);
            await pendingZoomSave; await Layout();
        }
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendPreviewWheel(nint window, uint message, nint wParam, nint lParam);
}
