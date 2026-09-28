namespace StudioX.Desktop;

using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Application.Plugins;
using StudioX.Engine;
using StudioX.Extensions;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>验证 WPF 控件与清理；可选样例包只在隔离工程执行，不连接硬件。</summary>
    public async Task RenderPluginsPreviewAsync(string directory, string? sampleArchive = null)
    {
        Width = 1460;
        Height = 1080;
        ShowDocument(ExtensionsTab);
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new InvalidOperationException(name);
            }
            checks.Add(name);
        }
        var manifest = new PluginManifest(1, 2, "studiox.preview", "1.0.0", "面板与命令示例", "offline.dll", "OfflinePlugin", ["commands", "panels"], []);
        PluginManager.CatalogList.ItemsSource = new[] { new PluginCatalogRow(new PluginCatalogEntry(manifest.Id, manifest,
            "offline/plugin.json", false, false, true, null), false) };
        PluginManager.EmptyHint.Visibility = Visibility.Collapsed;
        PluginManager.OperationStatus.Text = "离线界面验收：不加载插件程序集，不连接设备。";
        JsonElement Value(object value) => JsonSerializer.SerializeToElement(value);
        var panel = new PluginPanelDefinition("inspect", "工程观察面板", [
            new("intro", "text", "当前工程", Value("示例工程 · 只读快照")),
            new("usage", "metric", "缓冲区", Value("3 个文件 / 1 个未保存")),
            new("files", "table", "工程文件", Value(new[] { new[] { "src/main.c", "C", "已保存" }, new[] { "include/config.h", "C", "未保存" } }), Columns: ["路径", "类型", "状态"]),
            new("tree", "tree", "源码结构", Children: [new("source", "text", "src", Children: [new("main", "text", "main.c")])]),
            new("samples", "plot", "温度曲线 / °C", Value(Enumerable.Range(0, 60).Select(index => new { x = index, y = 24 + Math.Sin(index / 6d) }).ToArray())),
            new("settings", "form", "采集参数", Children: [
                new("label", "input", "显示名称", Value("温度")),
                new("interval", "number", "采样周期 / ms", Value(100)),
                new("visible", "checkbox", "显示曲线", Value(true)),
                new("channel", "select", "通道", Value(new { options = new[] { new { label = "ADC 1", value = "adc1" }, new { label = "ADC 2", value = "adc2" } }, selected = "adc2" })),
                new("apply", "button", "应用采集参数", CommandId: "apply")])]);
        JsonElement submitted = default;
        var renderer = new PluginPanelRenderer((command, arguments) =>
        {
            Check(command == "apply", "声明式按钮绑定命令 ID");
            submitted = arguments;
            return Task.CompletedTask;
        }, message => throw new InvalidOperationException(message));
        var content = renderer.Render(panel);
        PluginManager.PanelsHost.Children.Add(content);
        AddPluginContributions([new(manifest.Id, manifest, new([
            new("inspect", "读取当前文件", "palette"),
            new("capture", "记录快照", "toolbar"),
            new("analyze", "分析所选源码", "editorContext"),
            new("project", "汇总工程", "projectContext")], [], []))]);
        Check(PluginManager.CommandsHost.Children.Count == 4 && PluginToolbar.Children.Count == 1 &&
            pluginContextItems.Count == 2, "命令分别挂载列表、工具栏和两种上下文菜单");
        PluginManager.WorkspaceHint.Text = "示例贡献：命令、快捷键、表单、表格、树和曲线。";
        await Layout();
        var apply = PluginDescendants(content).OfType<Button>().Single(button => button.Content is string title && title == "应用采集参数");
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(submitted.ValueKind == JsonValueKind.Object && submitted.GetProperty("values").GetProperty("interval").GetDouble() == 100 &&
            submitted.GetProperty("values").GetProperty("visible").GetBoolean() &&
            submitted.GetProperty("values").GetProperty("channel").GetString() == "adc2", "表单保留数值、布尔和 select 原始 JSON 值");
        var labelInput = PluginDescendants(content).OfType<TextBox>().Single(input => input.Text == "温度");
        labelInput.Text = "用户修改的名称";
        var replacement = renderer.Render(panel);
        Check(PluginDescendants(replacement).OfType<TextBox>().Any(input => input.Text == "用户修改的名称"),
            "面板 publish 重绘保留用户已输入值");
        PluginManager.PanelsHost.Children.Clear();
        PluginManager.PanelsHost.Children.Add(replacement);
        renderer.SetEnabled(false);
        Check(!replacement.IsEnabled, "忙碌状态禁用面板交互");
        renderer.SetEnabled(true);
        try
        {
            renderer.Render(new("bad", "拒绝未知控件", [new("unknown", "xaml", "不可执行 XAML", Value("<Button/>"))]));
            throw new InvalidOperationException("未知 XAML 控件没有拒绝。");
        }
        catch (InvalidDataException)
        {
            Check(true, "渲染拒绝 XAML/HTML 等未知控件");
        }
        var command = new PluginUiCommand(() => Task.CompletedTask, () => false);
        var initialBindings = InputBindings.Count;
        RegisterPluginShortcut("Ctrl+S", "保留快捷键", command);
        Check(InputBindings.Count == initialBindings, "不覆盖 Ctrl+S 工作台快捷键");
        RegisterPluginShortcut("Ctrl+Alt+Shift+P", "插件示例", command);
        Check(InputBindings.Count == initialBindings + 1, "合法快捷键注册成功");
        RegisterPluginShortcut("Ctrl+Alt+Shift+P", "冲突插件", command);
        Check(InputBindings.Count == initialBindings + 1, "拒绝重复快捷键");
        var request = new StudioXMcpApprovalRequest("E:\\Offline\\PluginPreview", "project_edit_file",
            "插件 studiox.preview 请求写入 src/main.c；本验收不会执行工具。", StudioXMcpPermission.FileWrite);
        var approval = PluginManager.RequestApprovalAsync(request, () => true, CancellationToken.None);
        await Layout();
        Check(PluginManager.ApprovalHost.Children.Count == 1, "插件操作使用页内审批卡");
        var reject = PluginDescendants(PluginManager.ApprovalHost).OfType<Button>().Single(button => button.Content is string title && title == "拒绝本次");
        reject.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!await approval && PluginManager.ApprovalHost.Children.Count == 0, "选择后立即移除审批卡");
        using var cancellation = new CancellationTokenSource();
        var cancelledApproval = PluginManager.RequestApprovalAsync(request, () => true, cancellation.Token);
        cancellation.Cancel();
        Check(!await cancelledApproval && PluginManager.ApprovalHost.Children.Count == 0, "取消请求移除审批卡且不会批准");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            PluginManager.BringIntoView(new Rect(0, 0, PluginManager.ActualWidth, 400));
            await Layout();
            Render(this, Path.Combine(directory, "plugins-" + theme.Id + ".png"));
            PluginManager.PanelsHost.BringIntoView(new Rect(0, Math.Max(0, PluginManager.PanelsHost.ActualHeight - 450), PluginManager.PanelsHost.ActualWidth, 450));
            await Layout();
            Render(this, Path.Combine(directory, "plugin-panels-" + theme.Id + ".png"));
        }
        await StopPluginWorkspaceAsync();
        Check(InputBindings.Count == initialBindings && pluginBindings.Count == 0 &&
            PluginManager.PanelsHost.Children.Count == 0, "会话结束移除快捷键和面板");
        if (sampleArchive is not null)
        {
            await ValidatePluginEditorWorkspaceAsync(directory, sampleArchive, Check, Layout);
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            status = "passed",
            hardwareConnected = false,
            pluginAssembliesExecuted = sampleArchive is not null,
            checks
        }, new JsonSerializerOptions { WriteIndented = true }));
        async Task Layout()
        {
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        }
    }

    private async Task ValidatePluginEditorWorkspaceAsync(string directory, string archive,
        Action<bool, string> check, Func<Task> layout)
    {
        PluginManager.OperationStatus.Text = "隔离工作区验收：已执行本地示例插件，不连接设备。";
        var project = Path.Combine(directory, "fixture-project");
        try
        {
            Directory.CreateDirectory(Path.Combine(project, ".studiox"));
            Directory.CreateDirectory(Path.Combine(project, "src"));
            await JsonStore.WriteAsync(Path.Combine(project, ".studiox", "project.json"),
                new ProjectManifest(1, "plugin-editor-fixture", "offline.pack", "1.0.0", "offline", "offline", "blank", "offline", "1.0.0", "gcc"));
            var sourcePath = Path.Combine(project, "src", "main.c");
            await File.WriteAllTextAsync(sourcePath, "int main(void)\n{\n    return 0;\n}\n");
            var installed = await services.PluginManager.ImportAsync(archive);
            check(installed.Id == "studiox.workspace-overview" && !installed.Enabled, "真实示例安装后保持未启用");
            await services.PluginManager.SetEnabledAsync(installed.Id, true);
            projectDirectory = project;
            ShowSource(await services.Files.ReadAsync(project, "src/main.c"));
            await ReloadPluginWorkspaceAsync(CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(directory, "workspace-diagnostics.txt"), PluginManager.DiagnosticLog.Text);
            check(pluginWorkspace?.Contributions.Count == 1 && pluginWorkspace.LatestPanels.Count == 1 && pluginPanels.Count == 1,
                $"独立宿主激活、启动 publish 与 Desktop 面板接通 ({pluginWorkspace?.Contributions.Count}/{pluginWorkspace?.LatestPanels.Count}/{pluginPanels.Count})");
            var workspace = pluginWorkspace!;
            var generation = pluginGeneration;
            SourceEditor.AppendText("// 未保存的实时缓冲区\n");
            var snapshot = await pluginBroker!.CallAsync(installed.Id, "editor_read", JsonSerializer.SerializeToElement(new
            {
                path = "src/main.c"
            }));
            check(snapshot.GetProperty("IsDirty").GetBoolean() && snapshot.GetProperty("text").GetString()!.Contains("未保存的实时缓冲区", StringComparison.Ordinal),
                "editor_read 读取实时脏缓冲区而非旧磁盘");
            await File.WriteAllTextAsync(sourcePath, "// 另一个工具写入磁盘\n");
            await SynchronizePluginEditorsAsync(project, generation);
            check(SourceEditor.Text.Contains("未保存的实时缓冲区", StringComparison.Ordinal), "磁盘写入保留未保存编辑缓冲区");
            var clean = await services.Files.ReadAsync(project, "src/main.c");
            activeEditor!.Source = clean;
            activeEditor.Buffer.Text = clean.Text;
            await File.WriteAllTextAsync(sourcePath, "// 已保存文件的新内容\n");
            await SynchronizePluginEditorsAsync(project, generation);
            check(SourceEditor.Text == "// 已保存文件的新内容\n" && !activeEditor.IsDirty, "干净的已打开文件实时同步且保持保存状态");
            await pluginBroker.CallAsync(installed.Id, "editor_open", JsonSerializer.SerializeToElement(new
            {
                path = "src/main.c",
                line = 1,
                column = 4
            }));
            check(SourceEditor.CaretOffset == 3, "editor_open 在宿主编辑器中定位行列");
            ShowDocument(ExtensionsTab);
            using (var busy = new CancellationTokenSource())
            {
                aiCancellation = busy;
                RefreshPluginCommandState();
                check(!CanRunPluginCommand && pluginCommands.All(command => !command.CanExecute(null)), "AI 忙碌时禁止插件命令并发修改");
                aiCancellation = null;
            }
            await InvokePluginCommandAsync(installed.Id, "echo_form", JsonSerializer.SerializeToElement(new
            {
                values = new
                {
                    note = "来自真实 Desktop 表单"
                }
            }));
            FlushPluginPanels();
            check(workspace.LatestPanels.Values.Single().Widgets.Any(widget => widget.Id == "form_result" &&
                widget.Value?.GetRawText().Contains("Desktop", StringComparison.Ordinal) == true), "Desktop 命令往返独立插件并发布新面板");
            await layout();
            PluginManager.PanelsHost.BringIntoView(new Rect(0, Math.Max(0, PluginManager.PanelsHost.ActualHeight - 450), PluginManager.PanelsHost.ActualWidth, 450));
            await layout();
            Render(this, Path.Combine(directory, "plugin-runtime-workspace.png"));
            await services.PluginManager.SetEnabledAsync(installed.Id, false);
            await ReloadPluginWorkspaceAsync(CancellationToken.None);
            check(pluginWorkspace?.Contributions.Count == 0 && pluginPanels.Count == 0 && pluginBindings.Count == 0,
                "停用终止贡献并移除面板与绑定");
            PluginWorkspace_Changed(workspace, new(installed.Id, "panel", JsonSerializer.SerializeToElement(
                new PluginPanelDefinition("late", "过期面板", [new("late_text", "text", "不应出现")]), PluginJson)));
            FlushPluginPanels();
            check(pluginPanels.Count == 0, "旧工程会话迟到 publish 不会复活界面");
        }
        finally
        {
            await StopPluginWorkspaceAsync();
            // 隔离夹具的未保存修改不应在预览失败时弹出保存对话框。
            ClearEditorDocuments();
            projectDirectory = null;
        }
    }

    private static IEnumerable<DependencyObject> PluginDescendants(DependencyObject root)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in PluginDescendants(child))
            {
                yield return descendant;
            }
        }
    }
}
