namespace StudioX.Desktop;

using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using StudioX.Application.Plugins;
using StudioX.Engine;
using StudioX.Extensions.Abstractions;

public partial class MainWindow
{
    private PluginWorkspaceSession? pluginWorkspace;
    private PluginWorkspaceBroker? pluginBroker;
    private CancellationTokenSource? pluginWorkspaceCancellation;
    private CancellationTokenSource? pluginInvocationCancellation;
    private Task pluginInvocationTask = Task.CompletedTask;
    private int pluginGeneration;
    private int pluginCatalogDirty;
    private bool pluginCatalogRefreshing;
    private readonly List<KeyBinding> pluginBindings = [];
    private readonly List<MenuItem> pluginContextItems = [];
    private readonly List<PluginUiCommand> pluginCommands = [];
    private readonly HashSet<string> pluginStopped = new(StringComparer.Ordinal);
    private readonly Dictionary<KeyBinding, string> pluginBindingOwners = [];
    private readonly Dictionary<string, (PluginPanelRenderer Renderer, ContentControl Host)> pluginPanels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginPanelDefinition> pluginPendingPanels = new(StringComparer.Ordinal);
    private readonly object pluginEventLock = new();
    private readonly DispatcherTimer pluginUiTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly SemaphoreSlim pluginWorkspaceGate = new(1, 1);
    private static readonly JsonSerializerOptions PluginJson = new(JsonSerializerDefaults.Web);
    private bool CanRunPluginCommand => !closing && !closed && projectDirectory is not null &&
        pluginWorkspace is not null && pluginInvocationCancellation is null && aiCancellation is null && !projectActionsBusy;

    private void InitializePlugins()
    {
        PluginManager.Attach(services.PluginManager);
        PluginManager.WorkspaceChangedAsync = ReloadPluginWorkspaceAsync;
        PluginManager.CancelCommandRequested = () => pluginInvocationCancellation?.Cancel();
        PluginManager.ShowSettingsRequestedAsync = id => ShowPluginSettingsAsync(id);
        PluginManager.ShowDebugRequestedAsync = id => ShowPluginDebugAdaptersAsync(id);
        services.PluginManager.Changed += PluginCatalog_Changed;
        var palette = new PluginUiCommand(() =>
        {
            return ShowPaletteAsync();
        }, () => !closing && !closed);
        InputBindings.Add(new KeyBinding(palette, new KeyGesture(Key.P, ModifierKeys.Control | ModifierKeys.Shift)));
        pluginUiTimer.Tick += (_, _) =>
        {
            RefreshPluginCommandState();
            FlushPluginPanels();
            FlushPluginDocumentEvents();
            RefreshPluginCatalogIfChanged();
        };
        pluginUiTimer.Start();
        if (sourceContextMenu is not null)
        {
            sourceContextMenu.Opened += (_, _) => RefreshPluginCommandState();
        }
        if (explorerMenu is not null)
        {
            explorerMenu.Opened += (_, _) => RefreshPluginCommandState();
        }
    }

    private async Task ReloadPluginWorkspaceAsync(CancellationToken token)
    {
        await pluginWorkspaceGate.WaitAsync(token);
        try
        {
            await StopPluginWorkspaceCoreAsync();
            if (closing || closed || projectDirectory is not { } project)
            {
                return;
            }
            var generation = pluginGeneration;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            pluginWorkspaceCancellation = cancellation;
            var authorizer = new DesktopMcpAuthorizer(this,
                candidate => IsCurrentPluginProject(candidate, generation) && aiCancellation is null && !projectActionsBusy,
                async (request, requestToken) =>
                {
                    ShowDocument(ExtensionsTab);
                    return await PluginManager.RequestApprovalAsync(request,
                        () => IsCurrentPluginProject(project, generation) && aiCancellation is null && !projectActionsBusy, requestToken);
                });
            pluginBroker = new PluginWorkspaceBroker(services, project, authorizer,
                () => Dispatcher.InvokeAsync(() => editorDocuments.Any(document => document.IsDirty)).Task,
                new DesktopPluginEditorAccess(
                    async (relative, editorToken) => await Dispatcher.InvokeAsync(() =>
                    {
                        editorToken.ThrowIfCancellationRequested();
                        return IsCurrentPluginProject(project, generation) && FindEditor(relative) is { } document
                            ? new PluginEditorSnapshot(document.Source.RelativePath, document.Buffer.Text, document.IsDirty, document.Source.DiskHash)
                            : null;
                    }, DispatcherPriority.Normal, editorToken),
                    async (relative, line, column, editorToken) => await Dispatcher.InvokeAsync(async () =>
                    {
                        editorToken.ThrowIfCancellationRequested();
                        if (!IsCurrentPluginProject(project, generation))
                        {
                            return;
                        }
                        await OpenSourceAsync(relative, editorToken);
                        if (IsCurrentPluginProject(project, generation))
                        {
                            var targetLine = SourceEditor.Document.GetLineByNumber(Math.Clamp(line, 1, SourceEditor.Document.LineCount));
                            SourceEditor.CaretOffset = targetLine.Offset + Math.Clamp(column - 1, 0, targetLine.Length);
                            SourceEditor.ScrollToLine(targetLine.LineNumber);
                            SourceEditor.Focus();
                        }
                    }, DispatcherPriority.Normal, editorToken).Task.Unwrap()));
            var broker = pluginBroker;
            broker.OperationCompleted += (_, completed) =>
            {
                if (IsPluginWriteTool(completed.Tool))
                {
                    Dispatcher.BeginInvoke(async () =>
                    {
                        try
                        {
                            await SynchronizePluginEditorsAsync(project, generation);
                        }
                        catch (Exception error)
                        {
                            PluginManager.Log(error.ToString());
                        }
                    });
                }
            };
            var workspace = await services.PluginManager.OpenWorkspaceAsync(project,
                broker.CallAsync, cancellation.Token);
            if (!IsCurrentPluginProject(project, generation))
            {
                await workspace.DisposeAsync();
                return;
            }
            pluginWorkspace = workspace;
            workspace.Changed += PluginWorkspace_Changed;
            AddPluginContributions(workspace.Contributions);
            RefreshPluginContributionActions();
            await InitializePluginProductivityAsync(workspace, cancellation.Token);
            foreach (var (key, panel) in workspace.LatestPanels)
            {
                UpdatePluginPanel(key, panel);
            }
            foreach (var diagnostic in workspace.Diagnostics)
            {
                PluginManager.Log(diagnostic);
            }
            PluginManager.WorkspaceHint.Text = workspace.Contributions.Count == 0
                ? "没有运行中的通用插件；可安装后显式启用。"
                : $"已加载 {workspace.Contributions.Count} 个插件；" + (pluginActivities.Count > 0
                    ? $"其中 {pluginActivities.Count} 个在左侧注册了独立入口。" : "面板显示在此页面。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await StopPluginWorkspaceCoreAsync();
            throw;
        }
        catch (Exception error)
        {
            PluginManager.Log(error.ToString());
            PluginManager.WorkspaceHint.Text = "插件会话加载失败：" + error.Message;
            await StopPluginWorkspaceCoreAsync();
        }
        finally
        {
            pluginWorkspaceGate.Release();
        }
    }

    private void PluginCatalog_Changed(object? sender, EventArgs e) => Interlocked.Exchange(ref pluginCatalogDirty, 1);

    private async void RefreshPluginCatalogIfChanged()
    {
        if (closing || closed || pluginCatalogRefreshing || PluginManager.IsOperating ||
            Interlocked.Exchange(ref pluginCatalogDirty, 0) == 0)
        {
            return;
        }
        pluginCatalogRefreshing = true;
        try
        {
            await PluginManager.RefreshAsync();
        }
        catch (Exception error)
        {
            PluginManager.Log(error.ToString());
        }
        finally
        {
            pluginCatalogRefreshing = false;
        }
    }

    private void AddPluginContributions(IReadOnlyList<PluginActiveContribution> contributions)
    {
        foreach (var active in contributions)
        {
            AddPluginActivity(active);
            foreach (var definition in active.Contribution.Commands)
            {
                var command = new PluginUiCommand(
                    () => InvokePluginCommandAsync(active.Id, definition.Id, CapturePluginCommandContext()),
                    () => CanRunPluginCommand && !pluginStopped.Contains(active.Id));
                pluginCommands.Add(command);
                var caption = active.Manifest.DisplayName + " · " + definition.Title;
                var button = new Button { Content = caption, Command = command, Margin = new Thickness(0, 0, 8, 8), ToolTip = definition.Shortcut };
                PluginManager.CommandsHost.Children.Add(button);
                switch (definition.Placement)
                {
                    case "toolbar":
                    case "status":
                        PluginToolbar.Children.Add(new Button { Content = definition.Title, Command = command, ToolTip = caption, Padding = new Thickness(8, 4, 8, 4) });
                        break;
                    case "editorContext":
                    case "editor":
                        AddContext(sourceContextMenu, caption, command);
                        break;
                    case "projectContext":
                    case "project":
                        AddContext(explorerMenu, caption, command);
                        break;
                }
                RegisterPluginShortcut(definition.Shortcut, caption, command, active.Id);
            }
            foreach (var panel in active.Contribution.Panels)
            {
                UpdatePluginPanel(active.Id + "/" + panel.Id, panel);
            }
        }
        PluginManager.FilterCommands();
        RefreshPluginCommandState();
    }

    private void AddContext(ContextMenu? menu, string title, PluginUiCommand command)
    {
        if (menu is null)
        {
            return;
        }
        var item = new MenuItem { Header = "插件 · " + title, Command = command };
        menu.Items.Add(item);
        pluginContextItems.Add(item);
    }

    private void RegisterPluginShortcut(string? shortcut, string caption, PluginUiCommand command, string? pluginId = null)
    {
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            return;
        }
        try
        {
            if (new KeyGestureConverter().ConvertFromInvariantString(shortcut) is not KeyGesture gesture ||
                gesture.Modifiers == ModifierKeys.None ||
                InputBindings.OfType<KeyBinding>().Any(binding => binding.Key == gesture.Key && binding.Modifiers == gesture.Modifiers) ||
                gesture.Key is Key.F5 or Key.F7 or Key.F9 or Key.F10 or Key.F11 or Key.F12 ||
                gesture.Modifiers == ModifierKeys.Control && gesture.Key is Key.S or Key.O or Key.N or Key.C or Key.V or Key.X or Key.Z or Key.Y or Key.A)
            {
                PluginManager.Log($"未绑定快捷键 {shortcut}：与工作台保留操作冲突（{caption}）。");
                return;
            }
            var binding = new KeyBinding(command, gesture);
            InputBindings.Add(binding);
            pluginBindings.Add(binding);
            if (pluginId is not null)
            {
                pluginBindingOwners[binding] = pluginId;
            }
        }
        catch (Exception error) when (error is FormatException or ArgumentException or NotSupportedException)
        {
            PluginManager.Log($"快捷键 {shortcut} 无效：{error.Message}");
        }
    }

    private JsonElement CapturePluginCommandContext() => JsonSerializer.SerializeToElement(new
    {
        relativePath = activeEditor?.Source.RelativePath,
        selectionStart = activeEditor is null ? 0 : SourceEditor.SelectionStart,
        selectionLength = activeEditor is null ? 0 : SourceEditor.SelectionLength,
        projectEntryRelativePath = explorerMenuEntry?.RelativePath
    });

    private async Task InvokePluginCommandAsync(string pluginId, string commandId, JsonElement arguments)
    {
        if (!CanRunPluginCommand || pluginStopped.Contains(pluginId) || pluginWorkspace is not { } workspace || pluginWorkspaceCancellation is null)
        {
            return;
        }
        // 从管理页或命令面板调用时展示结果页面；编辑器和工程上下文命令保留原有焦点。
        if (pluginActivities.TryGetValue(pluginId, out var activity) && workspace.Contributions.Any(active =>
            active.Id == pluginId && active.Contribution.Panels.Length > 0 && active.Contribution.Commands.Any(command =>
                command.Id == commandId && command.Placement is "tools" or "palette")))
        {
            ShowDocument(activity.Tab);
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(pluginWorkspaceCancellation.Token);
        pluginInvocationCancellation = cancellation;
        RefreshPluginCommandState();
        try
        {
            pluginInvocationTask = workspace.InvokeAsync(pluginId, "command", commandId, arguments, cancellation.Token);
            await pluginInvocationTask;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            PluginManager.Log("插件命令已取消。");
        }
        catch (Exception error)
        {
            PluginManager.Log(error.ToString());
        }
        finally
        {
            pluginInvocationCancellation = null;
            RefreshPluginCommandState();
        }
    }

    private void PluginWorkspace_Changed(object? sender, PluginWorkspaceEvent update)
    {
        var expected = pluginWorkspace;
        var generation = pluginGeneration;
        if (!ReferenceEquals(sender, expected) || closing || closed)
        {
            return;
        }
        if (update.Kind == "panel")
        {
            try
            {
                var panel = update.Payload.Deserialize<PluginPanelDefinition>(PluginJson) ?? throw new InvalidDataException("插件面板数据为空。");
                lock (pluginEventLock)
                {
                    if (generation == pluginGeneration && ReferenceEquals(sender, pluginWorkspace))
                    {
                        pluginPendingPanels[update.PluginId + "/" + panel.Id] = panel;
                    }
                }
            }
            catch (Exception error)
            {
                Dispatcher.BeginInvoke(() => PluginManager.Log(error.ToString()));
            }
        }
        else
        {
            Dispatcher.BeginInvoke(async () =>
            {
                if (generation == pluginGeneration && ReferenceEquals(sender, pluginWorkspace))
                {
                    if (update.Kind is "stopped" or "crashed")
                    {
                        pluginStopped.Add(update.PluginId);
                        foreach (var binding in pluginBindingOwners.Where(pair => pair.Value == update.PluginId).Select(pair => pair.Key).ToArray())
                        {
                            InputBindings.Remove(binding);
                            pluginBindings.Remove(binding);
                            pluginBindingOwners.Remove(binding);
                        }
                        foreach (var key in pluginPanels.Keys.Where(key => key.StartsWith(update.PluginId + "/", StringComparison.Ordinal)).ToArray())
                        {
                            if (pluginPanels[key].Host.Parent is Panel parent)
                            {
                                parent.Children.Remove(pluginPanels[key].Host);
                            }
                            pluginPanels.Remove(key);
                        }
                        RemovePluginActivity(update.PluginId);
                        RefreshPluginContributionActions();
                        RefreshPluginCommandState();
                        if (pluginBroker is { } broker)
                        {
                            try
                            {
                                await broker.StopPluginAsync(update.PluginId);
                            }
                            catch (Exception error)
                            {
                                PluginManager.Log(error.ToString());
                            }
                        }
                    }
                    PluginManager.Log(update.PluginId + " · " + update.Kind + " · " + update.Payload.GetRawText());
                }
            });
        }
    }

    private void FlushPluginPanels()
    {
        KeyValuePair<string, PluginPanelDefinition>[] pending;
        lock (pluginEventLock)
        {
            pending = pluginPendingPanels.ToArray();
            pluginPendingPanels.Clear();
        }
        foreach (var (key, panel) in pending)
        {
            try
            {
                UpdatePluginPanel(key, panel);
            }
            catch (Exception error)
            {
                PluginManager.Log(error.ToString());
            }
        }
    }

    private void UpdatePluginPanel(string key, PluginPanelDefinition panel)
    {
        if (pluginStopped.Contains(key[..key.IndexOf('/')]))
        {
            return;
        }
        if (!pluginPanels.TryGetValue(key, out var existing))
        {
            var pluginId = key[..key.IndexOf('/')];
            existing = (new PluginPanelRenderer((command, arguments) => InvokePluginCommandAsync(pluginId, command, arguments), PluginManager.Log), new ContentControl());
            pluginPanels.Add(key, existing);
            var body = pluginActivities.TryGetValue(pluginId, out var activity) ? activity.Body : PluginManager.PanelsHost;
            body.Children.Add(existing.Host);
        }
        existing.Host.Content = existing.Renderer.Render(panel);
        existing.Renderer.SetEnabled(CanRunPluginCommand);
    }

    private void RefreshPluginCommandState()
    {
        PluginManager.SetCommandRunning(pluginInvocationCancellation is not null);
        foreach (var command in pluginCommands)
        {
            command.Refresh();
        }
        foreach (var panel in pluginPanels.Values)
        {
            panel.Renderer.SetEnabled(CanRunPluginCommand);
        }
    }

    private bool IsCurrentPluginProject(string project, int generation) => !closing && !closed && generation == pluginGeneration &&
        string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase);

    private static bool IsPluginWriteTool(string tool) => tool is "project_edit_file" or "project_patch_file" or
        "project_create_file" or "project_create_directory" or "external_project_copy" or "git_checkout" or "git_restore" or "ag32_pin_mapping_enable";

    private async Task SynchronizePluginEditorsAsync(string project, int generation)
    {
        foreach (var document in editorDocuments.ToArray())
        {
            if (!IsCurrentPluginProject(project, generation))
            {
                return;
            }
            if (!services.Files.FileExists(project, document.Source.RelativePath))
            {
                continue;
            }
            var disk = await services.Files.ReadAsync(project, document.Source.RelativePath);
            if (!IsCurrentPluginProject(project, generation))
            {
                return;
            }
            if (EditorSynchronizer.Apply(document, disk) == EditorDiskSyncResult.UnsavedChangesPreserved)
            {
                PluginManager.Log(document.Source.RelativePath + " 磁盘已变化，未保存的编辑缓冲区已保留。");
            }
        }
        if (IsCurrentPluginProject(project, generation))
        {
            await RefreshProjectTreeAsync();
            if (currentProjectManifest is not null && Ag32DeviceCatalog.Find(currentProjectManifest.DeviceId)?.CanMap == true)
            {
                await RefreshAg32PinMappingStatusAsync(CancellationToken.None);
            }
            Status.Text = "插件写入完成，编辑器和工程树已实时同步。";
        }
    }

    private async Task StopPluginWorkspaceAsync()
    {
        pluginWorkspaceCancellation?.Cancel();
        await pluginWorkspaceGate.WaitAsync();
        try
        {
            await StopPluginWorkspaceCoreAsync();
        }
        finally
        {
            pluginWorkspaceGate.Release();
        }
    }

    private async Task StopPluginWorkspaceCoreAsync()
    {
        await ClearPluginDebugViewsAsync();
        if (pluginWorkspace is { } previous)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await previous.PublishWorkspaceEventAsync("project.closing", new
                {
                    project = Path.GetFileName(previous.Project)
                }, deadline.Token);
            }
            catch (Exception error) { PluginManager.Log(error.ToString()); }
        }
        pendingDocumentEvents.Clear();
        pluginGeneration++;
        pluginWorkspaceCancellation?.Cancel();
        pluginInvocationCancellation?.Cancel();
        var workspace = pluginWorkspace;
        pluginWorkspace = null;
        RefreshPluginContributionActions();
        if (workspace is not null)
        {
            workspace.Changed -= PluginWorkspace_Changed;
            try
            {
                await workspace.DisposeAsync();
            }
            catch (Exception error)
            {
                // 一个宿主清理失败不能留下另一个宿主的快捷键、审批或设备观察会话。
                PluginManager.Log(error.ToString());
            }
        }
        try
        {
            await pluginInvocationTask;
        }
        catch (Exception error)
        {
            PluginManager.Log(error.ToString());
        }
        if (pluginBroker is not null)
        {
            try
            {
                await pluginBroker.DisposeAsync();
            }
            catch (Exception error)
            {
                PluginManager.Log(error.ToString());
            }
            pluginBroker = null;
        }
        pluginWorkspaceCancellation?.Dispose();
        pluginWorkspaceCancellation = null;
        foreach (var binding in pluginBindings)
        {
            InputBindings.Remove(binding);
        }
        foreach (var item in pluginContextItems)
        {
            sourceContextMenu?.Items.Remove(item);
            explorerMenu?.Items.Remove(item);
        }
        pluginBindings.Clear();
        pluginBindingOwners.Clear();
        pluginStopped.Clear();
        pluginContextItems.Clear();
        pluginCommands.Clear();
        pluginPanels.Clear();
        ClearPluginActivities();
        lock (pluginEventLock)
        {
            pluginPendingPanels.Clear();
        }
        PluginToolbar.Children.Clear();
        PluginManager.CommandsHost.Children.Clear();
        PluginManager.PanelsHost.Children.Clear();
        PluginManager.WorkspaceHint.Text = "打开工程后加载已启用插件的命令与面板。";
    }
}
