namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StudioX.Application.CodeIntelligence;
using StudioX.Application.Editing;

public partial class MainWindow
{
    private readonly List<WorkbenchCommand> workbenchCommands = [];
    private bool CanUseLanguageEditing => CanEditSource && CanNavigateCode && !IsPythonDocument && services.Intelligence.IsReady && !projectActionsBusy;
    private void InitializeProductivity()
    {
        void Add(MenuItem menu, string title, string shortcut, Func<bool> enabled, Func<Task> run)
        {
            var command = new WorkbenchCommand(title, shortcut, enabled, run);
            workbenchCommands.Add(command);
            menu.Items.Add(new MenuItem { Header = title, InputGestureText = shortcut, Command = new PluginUiCommand(run, enabled) });
        }
        Add(EditorMenu, "格式化文件", "Ctrl+Alt+F", () => CanUseLanguageEditing, () => PreviewFormatAsync(false));
        Add(EditorMenu, "格式化选区", "", () => CanUseLanguageEditing && SourceEditor.SelectionLength > 0, () => PreviewFormatAsync(true));
        Add(EditorMenu, "快捷修复…", "Alt+Enter", () => CanUseLanguageEditing, ShowQuickFixesAsync);
        Add(EditorMenu, "本地历史…", "", () => activeEditor is not null && projectDirectory is not null, ShowLocalHistoryAsync);
        Add(WorkspaceViewMenu, "快速打开文件…", "Ctrl+P", () => projectDirectory is not null, () => ShowQuickOpenAsync(false));
        Add(WorkspaceViewMenu, "搜索工程符号…", "Ctrl+T", () => services.Intelligence.IsReady, () => ShowQuickOpenAsync(true));
        Add(WorkspaceViewMenu, "命令面板…", "Ctrl+Shift+P", () => true, ShowPaletteAsync);
        Add(WorkspaceViewMenu, "移到另一编辑分组", "Ctrl+Alt+Right", () => activeEditor is not null, () => { MoveEditorGroup(activeEditor!, activeEditor!.Group == 0 ? 1 : 0); return Task.CompletedTask; });
        Add(WorkspaceViewMenu, "合并编辑分组", "", () => secondaryTabs?.Items.Count > 0, () => { MergeEditorGroups(); return Task.CompletedTask; });
        Add(DevelopmentComponentsMenu, "开发环境组件管理…", "", () => !projectActionsBusy, () => ShowToolManagementAsync());
        Add(DevelopmentComponentsMenu, "准备工程开发环境组件…", "", () => !projectActionsBusy, () => ShowProjectToolsAsync());
        DevelopmentComponentsMenu.Items.Add(new Separator());
        Add(DevelopmentComponentsMenu, "从 GitHub 获取开发环境组件…", "", () => !projectActionsBusy, ShowGithubComponentLibraryAsync);
        Add(DevelopmentComponentsMenu, "导入开发环境组件…", "", () => !projectActionsBusy, ImportDevelopmentComponentAsync);
        DevelopmentComponentsMenu.Items.Add(new Separator());
        Add(DevelopmentComponentsMenu, "校验当前工程的开发环境组件", "", () => projectDirectory is not null && !projectActionsBusy, VerifyProjectComponentsAsync);
        Add(ProjectToolsMenu, "工程健康检查…", "", () => !projectActionsBusy, () => ShowProjectHealthAsync());
        Add(DebugTopMenu, "固件故障分析…", "", () => !projectActionsBusy, ShowFaultAnalysisAsync);
        Add(DebugTopMenu, "调试连接记录…", "", () => true, () =>
        {
            new DebugConnectionWindow(services.DebugLaunch, "最近一次连接与恢复记录", false, (_, _) => Task.CompletedTask, review: true) { Owner = this }.ShowDialog();
            return Task.CompletedTask;
        });
        Add(ProjectToolsMenu, "构建历史与对比…", "", () => projectDirectory is not null && !projectActionsBusy, ShowBuildHistoryAsync);
        Add(ExtensionsMenu, "软件与组件分发…", "", () => !projectActionsBusy, ShowDistributionAsync);
        Add(ExtensionsMenu, "插件设置…", "", () => pluginWorkspace is not null, () => ShowPluginSettingsAsync());
        Add(ExtensionsMenu, "调试快照扩展…", "", () => pluginWorkspace is not null, () => ShowPluginDebugAdaptersAsync());
        Add(HelpRootMenu, "帮助手册…", "F1", () => true, () => ShowHelpAsync());
        Add(HelpRootMenu, "快捷键速查…", "", () => true, () => ShowHelpAsync("shortcuts"));
        Add(HelpRootMenu, "故障处理指南…", "", () => true, () => ShowHelpAsync("troubleshooting"));
        Add(HelpRootMenu, "工程健康检查…", "", () => !projectActionsBusy, () => ShowProjectHealthAsync());
        InputBindings.Add(new KeyBinding(new PluginUiCommand(() => ShowHelpAsync(), () => true), new KeyGesture(Key.F1)));
        workbenchCommands.Add(new("在工程中查找", "Ctrl+Shift+F", () => projectDirectory is not null, () => { ShowWorkspaceSearch(false); return Task.CompletedTask; }));
        workbenchCommands.Add(new("保存全部文件", "Ctrl+Shift+S", () => projectDirectory is not null, () => RunAsync(t => SaveAllSourcesAsync(RequireProject(), t))));
        InputBindings.Add(new KeyBinding(new PluginUiCommand(() => ShowQuickOpenAsync(false), () => projectDirectory is not null), new KeyGesture(Key.P, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(new PluginUiCommand(() => ShowQuickOpenAsync(true), () => services.Intelligence.IsReady), new KeyGesture(Key.T, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(new PluginUiCommand(() => PreviewFormatAsync(false), () => CanUseLanguageEditing), new KeyGesture(Key.F, ModifierKeys.Control | ModifierKeys.Alt)));
        InputBindings.Add(new KeyBinding(new PluginUiCommand(ShowQuickFixesAsync, () => CanUseLanguageEditing), new KeyGesture(Key.OemPeriod, ModifierKeys.Control)));
        // 保留 Ctrl+.，并提供不依赖标点键映射的入口。
        InputBindings.Add(new KeyBinding(new PluginUiCommand(ShowQuickFixesAsync, () => CanUseLanguageEditing), new KeyGesture(Key.Enter, ModifierKeys.Alt)));
        InputBindings.Add(new KeyBinding(new PluginUiCommand(() => { if (activeEditor is not null) { MoveEditorGroup(activeEditor, 1 - activeEditor.Group); } return Task.CompletedTask; }, () => activeEditor is not null), new KeyGesture(Key.Right, ModifierKeys.Control | ModifierKeys.Alt)));
        if (sourceContextMenu is not null)
        {
            foreach (var item in workbenchCommands.Take(4))
            {
                sourceContextMenu.Items.Add(new MenuItem { Header = item.Title, InputGestureText = item.Shortcut, Command = new PluginUiCommand(item.Execute, item.Enabled) });
            }
        }
        InitializeCodeTemplates();
    }
    private Task PreviewFormatAsync(bool selection) => RunAsync(async token =>
    {
        if (activeEditor is not { } editor)
        {
            return;
        }
        var project = RequireProject();
        var changes = await services.Intelligence.FormatAsync(new(editor.Source, editor.Buffer.Text), selection ? SourceEditor.SelectionStart : null, SourceEditor.SelectionLength, CaptureCodeDocuments(), token);
        ShowEditPlan(project, changes, selection ? "格式化选区（格式器可能调整相邻语句，请检查差异）" : "格式化文件");
    });
    private async Task ShowQuickFixesAsync()
    {
        if (activeEditor is not { } editor || !CanUseLanguageEditing)
        {
            return;
        }
        var project = RequireProject();
        var buffer = new WorkspaceBufferSnapshot(editor.Source, editor.Buffer.Text);
        var offset = SourceEditor.CaretOffset;
        IReadOnlyList<CodeActionPlan> fixes = [];
        await RunAsync(async token => fixes = await services.Intelligence.QuickFixesAsync(buffer, offset, CaptureCodeDocuments(), token));
        if (fixes.Count == 0)
        {
            Status.Text = "此位置暂无可直接应用的快捷修复；可在错误所在行重试。";
            return;
        }
        var picker = new QuickPickWindow("快捷修复", (q, _) => Task.FromResult<IReadOnlyList<QuickPickItem>>(fixes.Where(f => f.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).Select(f => new QuickPickItem(f.Title, "预览修改", f)).ToArray())) { Owner = this };
        if (picker.ShowDialog() == true && picker.Selected?.Value is CodeActionPlan fix)
        {
            ShowEditPlan(project, fix.Changes, fix.Title);
        }
    }
    private void ShowEditPlan(string project, IReadOnlyList<WorkspaceFileChange> changes, string title)
    {
        if (project != projectDirectory)
        {
            return;
        }
        if (changes.Count == 0)
        {
            Status.Text = "没有需要应用的修改。";
            return;
        }
        workspaceEditProject = project;
        WorkspaceSearch.SetResult(changes, title + " · 审阅后应用，可撤销", true);
        WorkspaceSearch.ResultTabs.SelectedIndex = 1;
        ShowDocument(WorkspaceSearchTab);
    }
    private async Task ShowQuickOpenAsync(bool symbols)
    {
        if (projectDirectory is not { } project)
        {
            return;
        }
        using var discoveryLifetime = new CancellationTokenSource();
        Task<WorkspaceFileIndex>? fileIndex = null;
        var picker = new QuickPickWindow(symbols ? "搜索工程符号" : "快速打开文件", async (query, token) => symbols
            ? (await services.Intelligence.SearchSymbolsAsync(query, token)).Select(l => new QuickPickItem(l.DisplayPath, $"第 {l.Range.Start.Line + 1} 行", l)).ToArray()
            : (await (await (fileIndex ??= services.WorkspaceDiscovery.CreateIndexAsync(project, discoveryLifetime.Token)).WaitAsync(token))
                .SearchAsync(query, token)).Select(p => new QuickPickItem(Path.GetFileName(p), p, p)).ToArray())
        {
            Owner = this
        };
        bool accepted;
        try
        {
            accepted = picker.ShowDialog() == true;
        }
        finally
        {
            // 输入取消只撤销旧查询，关闭窗口才停止共享扫描；任务结束前不释放其取消源。
            discoveryLifetime.Cancel();
            if (fileIndex is not null)
            {
                try
                {
                    await fileIndex;
                }
                catch (OperationCanceledException) when (discoveryLifetime.IsCancellationRequested) { }
                catch (Exception ex) { Log(ex.ToString()); }
            }
        }
        if (!accepted || project != projectDirectory)
        {
            return;
        }
        await RunAsync(token => picker.Selected?.Value is CodeLocation location ? JumpToCodeAsync(location, token) : OpenSourceAsync((string)picker.Selected!.Value, token));
    }
    private async Task ShowPaletteAsync()
    {
        var commands = workbenchCommands.Where(c => c.Title != "命令面板…").ToList();
        void Gather(MenuItem menu, string prefix)
        {
            if (menu.Visibility != Visibility.Visible)
            {
                return;
            }
            var title = menu.Header?.ToString() ?? "";
            if (menu.HasItems)
            {
                foreach (var child in menu.Items.OfType<MenuItem>())
                {
                    Gather(child, prefix.Length == 0 ? title : prefix + " · " + title);
                }
                return;
            }
            if (workbenchCommands.Any(c => c.Title == title))
            {
                return;
            }
            commands.Add(new(prefix + " · " + title, menu.InputGestureText, () => menu.IsEnabled, () =>
            {
                if (menu.Command is RoutedCommand routed)
                {
                    SourceEditor.Focus();
                    if (routed.CanExecute(menu.CommandParameter, SourceEditor))
                    {
                        routed.Execute(menu.CommandParameter, SourceEditor);
                    }
                }
                else if (menu.Command is { } command)
                {
                    if (command.CanExecute(menu.CommandParameter))
                    {
                        command.Execute(menu.CommandParameter);
                    }
                }
                else
                {
                    menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, menu));
                }
                return Task.CompletedTask;
            }));
        }
        foreach (var menu in MainCommandMenu.Items.OfType<MenuItem>())
        {
            Gather(menu, "");
        }
        if (ActivePluginContributions.Length > 0)
        {
            foreach (var plugin in ActivePluginContributions)
            {
                foreach (var command in plugin.Contribution.Commands)
                {
                    commands.Add(new("插件 · " + command.Title, command.Shortcut ?? plugin.Id, () => CanRunPlugin(plugin.Id), () => InvokePluginCommandAsync(plugin.Id, command.Id, CapturePluginCommandContext(plugin.Id))));
                }
            }
        }
        var picker = new QuickPickWindow("命令面板", (q, _) => Task.FromResult<IReadOnlyList<QuickPickItem>>(commands.Where(c => c.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(c => new QuickPickItem(c.Title, c.Shortcut + (c.Enabled() ? "" : " · 当前不可用"), c)).ToArray()))
        {
            Owner = this
        };
        if (picker.ShowDialog() == true && picker.Selected?.Value is WorkbenchCommand selected && selected.Enabled())
        {
            await selected.Execute();
        }
    }
    private async Task ShowLocalHistoryAsync()
    {
        if (activeEditor is not { } editor || projectDirectory is not { } project)
        {
            return;
        }
        IReadOnlyList<LocalHistoryEntry>? versions = null;
        await RunAsync(async token => versions = await services.LocalHistory.ListAsync(project, editor.Source.RelativePath, token));
        if (versions is null || project != projectDirectory)
        {
            return;
        }
        var picker = new QuickPickWindow("本地历史 — " + editor.Source.RelativePath, (q, _) => Task.FromResult<IReadOnlyList<QuickPickItem>>(versions.Select(v =>
            new QuickPickItem(v.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), v.Reason + $" · {v.Text.Length} 字符", v)).Where(i => i.ToString().Contains(q, StringComparison.OrdinalIgnoreCase)).ToArray()))
        {
            Owner = this
        };
        if (picker.ShowDialog() == true && picker.Selected?.Value is LocalHistoryEntry entry)
        {
            ShowEditPlan(project, [LocalHistoryService.RestorePlan(new(editor.Source, editor.Buffer.Text), entry)], "恢复本地历史 " + entry.CreatedUtc.ToLocalTime());
        }
    }
}
