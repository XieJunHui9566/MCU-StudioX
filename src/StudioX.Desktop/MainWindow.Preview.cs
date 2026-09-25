namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.Mcp;

public partial class MainWindow
{
    /// <summary>使用隔离用户数据，只读取指定工程，检查编辑器的文件类型和界面渲染。</summary>
    public async Task RenderEditorPreviewAsync(string directory, string project)
    {
        foreach (var dark in new[] { true, false })
            foreach (var language in new[] { "C", "C++", "CMake", "Assembly", "Linker", "JSON", "XML", "Verilog", "AGM Pin Map" }) _ = CodeLanguage.Get(language, dark);
        await OpenProjectAsync(project, CancellationToken.None);
        var root = (System.Windows.Controls.TreeViewItem)ProjectTree.Items[0];
        void Expand(System.Windows.Controls.TreeViewItem node, string path)
        {
            foreach (var child in node.Items.OfType<System.Windows.Controls.TreeViewItem>())
            {
                if (child.Tag is not ProjectEntry entry || !entry.IsDirectory || !path.StartsWith(entry.RelativePath, StringComparison.Ordinal)) continue;
                child.IsExpanded = true;
                if (path != entry.RelativePath) Expand(child, path);
                return;
            }
        }
        Expand(root, "device/sdk/startup");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme); UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(this, Path.Combine(directory, "editor-" + theme.Id + ".png"));
        }
        ApplyTheme(ThemeService.Dark);
        var savedEditor = editorSettings;
        foreach (var strength in new[] { 0d, 40d, 80d, 100d })
        {
            ApplyEditorSettings(savedEditor with { SoftGlow = true, GlowStrength = strength }); UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(SourceEditor, Path.Combine(directory, $"glow-{strength:0}.png"));
        }
        ApplyEditorSettings(savedEditor);
        RenderGlowComparison(directory);
        foreach (var relative in new[] { "device/sdk/include/system.h", "CMakeLists.txt", "device/CMakeLists.txt", "device/platform.cmake" })
        {
            if (!File.Exists(Path.Combine(project, relative))) continue;
            await OpenSourceAsync(relative, CancellationToken.None); UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (activeDocument?.ReadOnlyReason is not null)
            {
                var before = SourceEditor.Text;
                SourceEditor.TextArea.PerformTextInput("should-not-insert");
                if (!SourceEditor.IsReadOnly || SourceEditor.Text != before) throw new InvalidOperationException("器件支持配置未保持只读。");
            }
            Render(this, Path.Combine(directory, relative.Replace('/', '_') + ".png"));
        }
        await OpenSourceAsync("CMakeLists.txt", CancellationToken.None); UpdateLayout();
        if (SourceEditor.IsReadOnly) throw new InvalidOperationException("用户根配置不应只读。");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Render(this, Path.Combine(directory, "user-cmake.png"));
        var settings = new AppearanceWindow(services, currentTheme, backgroundSettings, editorSettings,
            (theme, background, editor) => { ApplyTheme(theme); ApplyBackground(background); ApplyEditorSettings(editor); })
        { Owner = this, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000 };
        settings.Show(); settings.UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Render(settings, Path.Combine(directory, "editor-settings.png")); settings.Close();
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "Loaded all nine syntax definitions (dark/light). Rendered source, header, CMake and editor settings. Project files were not modified.\n");
    }
    /// <summary>只渲染界面供布局检查；不创建工程、连接设备或启动构建。</summary>
    public async Task RenderPreviewAsync(string directory)
    {
        ShowDocument(WelcomeTab);
        // 预览等待后台记录清理完成，确保截图反映欢迎页的最终状态。
        await recentPruneTask;
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme); UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(this, Path.Combine(directory, theme.Id + ".png"));
        }
        ApplyTheme(ThemeService.Dark);
        var settings = new AppearanceWindow(services, currentTheme, backgroundSettings, editorSettings, (theme, background, editor) => { ApplyTheme(theme); ApplyBackground(background); ApplyEditorSettings(editor); })
        { Owner = this, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000 };
        settings.Show(); settings.UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Render(settings, Path.Combine(directory, "appearance.png")); settings.Close();
        AiSidebarColumn.Width = new GridLength(400);
        AiSidebarSplitterColumn.Width = new GridLength(5);
        AiSidebar.Visibility = AiSidebarSplitter.Visibility = Visibility.Visible;
        AiTranscript.Visibility = AiStatus.Visibility = AiPromptPanel.Visibility = Visibility.Visible;
        AiUnconfiguredHint.Visibility = Visibility.Collapsed;
        AiHistoryButton.IsEnabled = AiNewConversationButton.IsEnabled = true;
        aiConversationPickerUpdating = true;
        AiConversationPicker.ItemsSource = new[]
        {
            new AiConversation(Guid.NewGuid().ToString("N"), "检查工程启动代码", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []),
            new AiConversation(Guid.NewGuid().ToString("N"), "定位编译错误", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(-1), []),
            new AiConversation(Guid.NewGuid().ToString("N"), "核对串口数据", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-2), [])
        };
        AiConversationPicker.SelectedIndex = 0;
        aiConversationPickerUpdating = false;
        AiConversationHeading.Text = "检查工程启动代码";
        AiConversationHeading.Visibility = Visibility.Visible;
        AiHistoryCount.Text = "3 条";
        aiDisplaySettings = new AiSettings(ReasoningEffort: "max");
        AiPopupModelName.Text = "deepseek-flash";
        aiThinkingSliderUpdating = true;
        AiThinkingSlider.Value = 3;
        aiThinkingSliderUpdating = false;
        UpdateAiThinkingLabel();
        AiTranscriptItems.Children.Clear();
        AppendAiTranscript("你", "检查一下工程启动代码，指出可能的时钟配置问题。");
        AppendAiTranscript("AI", "我可以检查当前工程的启动文件和系统时钟设置。先读取相关源码，再指出具体位置与修改建议。\n\n当前示例仅用于界面预览。");
        UpdateAiContextMeter(new AiSettings(ReasoningEffort: "max"), 32768, hasRequest: true,
            cacheHitTokens: 24_576, cacheMissTokens: 8_192);
        AiStatus.Text = "已完成。";
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme); UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(this, Path.Combine(directory, "ai-" + theme.Id + ".png"));
        }
        ApplyTheme(ThemeService.Dark);
        AiSidebarColumn.Width = new GridLength(320);
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Render(this, Path.Combine(directory, "ai-compact.dark.png"));
        AiHistoryPopup.IsOpen = true;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        if (AiHistoryPopup.Child is not FrameworkElement historyPanel ||
            historyPanel.ActualWidth <= 0 || historyPanel.ActualHeight <= 0)
            throw new InvalidOperationException("AI 历史弹层未完成布局，无法验证选中样式。");
        Render(historyPanel, Path.Combine(directory, "ai-history.dark.png"));
        AiHistoryPopup.IsOpen = false;
        StartAiActivity();
        aiProgressBuffer?.Report(new AiAgentProgress(AiAgentProgressKind.ModelRequestStarted, 1));
        aiProgressBuffer?.Report(new AiAgentProgress(AiAgentProgressKind.ReasoningDelta, 1,
            "离线布局示例：这里显示接口实际返回的思考内容。"));
        aiProgressBuffer?.Report(new AiAgentProgress(AiAgentProgressKind.ToolCallStarted, 1,
            ToolName: "project_read_file"));
        DrainAiActivityProgress();
        if (aiActivityReasoning is not null) aiActivityReasoning.IsExpanded = true;
        AiActivityTimer_Tick(null, EventArgs.Empty);
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Render(this, Path.Combine(directory, "ai-working.dark.png"));
        FinishAiActivity();
        var previousProject = projectDirectory;
        using var previewApprovalCancellation = new CancellationTokenSource();
        Task<bool>? previewApproval = null;
        try
        {
            // 只渲染卡片；假工程路径不会传给 MCP，也不会执行文件或设备操作。
            var previewProject = Path.Combine(directory, "approval-layout-only");
            projectDirectory = previewProject;
            StartAiActivity();
            previewApproval = RequestAiMcpApprovalAsync(
                new StudioXMcpApprovalRequest(previewProject, "project_edit_file",
                    "将 src/main.c 中的 LED 闪烁间隔改为 200 ms。仅用于界面预览。",
                    StudioXMcpPermission.FileWrite), aiProjectGeneration, previewApprovalCancellation.Token);
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(this, Path.Combine(directory, "ai-approval.dark.png"));
        }
        finally
        {
            previewApprovalCancellation.Cancel();
            var incorrectlyApproved = previewApproval is not null && await previewApproval;
            FinishAiActivity();
            projectDirectory = previousProject;
            if (incorrectlyApproved) throw new InvalidOperationException("离线预览不得批准 MCP 操作。");
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "Rendered welcome, appearance settings, and AI sidebar. No device/build/API actions.\n");
    }
    private static void Render(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var context = backdrop.RenderOpen()) context.DrawRectangle((Brush)System.Windows.Application.Current.Resources["Background"], null, new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
        bitmap.Render(backdrop);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static void RenderGlowComparison(string directory)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(30, 31, 34)), null, new Rect(0, 0, 1160, 530));
            for (var index = 0; index < 2; index++)
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(Path.Combine(directory, index == 0 ? "glow-0.png" : "glow-80.png")); bitmap.EndInit();
                var label = new FormattedText(index == 0 ? "柔光关闭" : "柔光开启 · 80%", System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 16, Brushes.White, 1);
                context.DrawText(label, new Point(index * 580 + 22, 16));
                context.DrawImage(new CroppedBitmap(bitmap, new Int32Rect(0, 24, 570, 470)), new Rect(index * 580, 50, 570, 470));
            }
        }
        var output = new RenderTargetBitmap(1160, 530, 96, 96, PixelFormats.Pbgra32); output.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(output));
        using var file = File.Create(Path.Combine(directory, "glow-comparison.png")); encoder.Save(file);
    }
}
