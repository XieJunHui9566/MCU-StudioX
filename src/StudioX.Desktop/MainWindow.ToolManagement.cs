namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StudioX.Application;
using StudioX.Application.Tools;

public partial class MainWindow
{
    private TabItem? toolManagementTab;
    private ToolManagementView? toolManagementView;
    private string? toolManagementProject;
    private async Task ImportDevelopmentComponentAsync()
    {
        await ShowToolManagementAsync();
        await RunToolManagementActionAsync("install");
    }
    private Task ShowToolManagementAsync(string? checkedProject = null) => RunAsync(async token =>
    {
        toolManagementProject = checkedProject ?? projectDirectory;
        if (toolManagementTab is null || !WorkspaceTabs.Items.Contains(toolManagementTab))
        {
            toolManagementView = new() { Requested = RunToolManagementActionAsync };
            toolManagementTab = AddToolTab("开发环境组件管理", toolManagementView);
        }
        ShowDocument(toolManagementTab);
        await RefreshToolManagementAsync(token);
    });
    private async Task RefreshToolManagementAsync(CancellationToken token)
    {
        toolManagementView!.SetBusy(true, "正在统计目录与核对依赖…");
        try { toolManagementView.SetReport(await services.ToolManagement.InspectAsync(toolManagementProject, token: token)); }
        finally { toolManagementView.SetBusy(false); }
    }
    private Task RunToolManagementActionAsync(string action)
    {
        if (action == "help") return ShowHelpAsync("tool-environment");
        if (action == "github") return ShowGithubComponentLibraryAsync();
        if (action == "prepare") return ShowProjectToolsAsync(toolManagementProject);
        return RunAsync(async token =>
        {
            if (action == "refresh") { await RefreshToolManagementAsync(token); return; }
            if (action == "register")
            {
                var dialog = new OpenFolderDialog { Title = "登记需要保留工具依赖的 StudioX 工程" };
                if (dialog.ShowDialog(this) != true) return;
                await services.ToolManagement.RegisterProjectAsync(dialog.FolderName, token);
                await RefreshToolManagementAsync(token);
                return;
            }
            if (action == "scope") { await ShowToolManagementScopeAsync(token); return; }
            if (action == "export")
            {
                if (toolManagementView?.Report is not { } report) return;
                var dialog = new SaveFileDialog { Filter = "工具占用报告|*.json", FileName = "studiox-tool-usage.json" };
                if (dialog.ShowDialog(this) == true) await services.ToolManagement.ExportReportAsync(report, dialog.FileName, token);
                return;
            }
            var progressEnabled = true;
            toolManagementView!.SetBusy(true);
            var progress = new Progress<string>(text => { if (progressEnabled) toolManagementView.SetBusy(true, text); });
            try
            {
                if (action == "install")
                {
                    var dialog = new OpenFileDialog { Title = "导入开发环境组件", Filter = DevelopmentComponentDialogs.ImportFilter };
                    if (dialog.ShowDialog(this) != true) return;
                    var preview = await services.ToolManagement.PreviewInstallAsync(dialog.FileName, progress, token);
                    if (MessageBox.Show(this, preview.ToText(), "确认开发环境组件", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                    var result = await services.ToolManagement.InstallAsync(preview, progress, token);
                    Log($"{(result.AlreadyInstalled ? "相同开发环境组件已安装且校验通过" : "已并存安装开发环境组件")}：{result.Identity.Key}；已有工程锁定未修改。");
                }
                else if (toolManagementView.SelectedVersion is { } selected)
                {
                    if (action is "component-export" or "repair")
                    {
                        var entry = new ToolEnvironmentEntry(selected.Id, selected.Version, selected.Name, selected.CompilerId, selected.Bytes, selected.Files, false, "");
                        if (action == "component-export")
                        {
                            var dialog = new SaveFileDialog { Filter = DevelopmentComponentDialogs.ExportFilter, FileName = selected.Id + "-" + selected.Version + ".mcutoolchain", DefaultExt = ".mcutoolchain", AddExtension = true };
                            if (dialog.ShowDialog(this) != true) return;
                            await services.ToolEnvironment.ExportAsync(entry, dialog.FileName, progress, token);
                            Log("开发环境组件已导出：" + dialog.FileName);
                        }
                        else
                        {
                            var dialog = new OpenFileDialog { Title = "修复所选开发环境组件", Filter = DevelopmentComponentDialogs.ImportFilter };
                            if (dialog.ShowDialog(this) != true) return;
                            var backup = await services.ToolEnvironment.RepairAsync(entry, dialog.FileName, progress, token);
                            Log("开发环境组件已修复。" + (backup.Length > 0 ? "原文件备份：" + backup : ""));
                        }
                        progressEnabled = false;
                        await RefreshToolManagementAsync(token);
                        return;
                    }
                    if (action == "toggle")
                    {
                        if (selected.Enabled && selected.References.Count > 0 && MessageBox.Show(this,
                            ComponentImpact(selected) + "\n\n禁用保留文件和工程锁定；需要此版本的工程或功能会暂时无法使用。可随时重新启用。",
                            "禁用开发环境组件", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                        await services.ToolManagement.SetEnabledAsync(selected, !selected.Enabled, toolManagementProject, token);
                        Log($"开发环境组件 {selected.Id} / {selected.Version} 已{(selected.Enabled ? "禁用" : "启用")}。文件与工程锁保留。" );
                        progressEnabled = false;
                        await RefreshToolManagementAsync(token);
                        return;
                    }
                    if (action == "debug-check")
                    {
                        var diagnostic = await services.ToolManagement.DiagnoseDebugEnvironmentAsync(selected, toolManagementProject, progress, token);
                        progressEnabled = false;
                        toolManagementView.DetailText.Text = diagnostic;
                        toolManagementView.SetBusy(false, "调试环境检查通过；未连接设备。");
                        Log(diagnostic);
                        return;
                    }
                    if (action == "verify")
                    {
                        await services.ToolEnvironment.VerifyAsync(new(selected.Id, selected.Version, selected.Name, selected.CompilerId, selected.Bytes, selected.Files, false, ""), progress, token);
                        progressEnabled = false;
                        Status.Text = "完整校验通过：" + selected.Id + " / " + selected.Version;
                        toolManagementView.SetBusy(false, Status.Text);
                        if (toolManagementView.Report is { } verifiedReport)
                        {
                            var verifiedEntry = selected with { Diagnostic = "本次完整哈希校验通过。目录变化或刷新后需重新核验。" };
                            toolManagementView.SetReport(verifiedReport with { Versions = verifiedReport.Versions.Select(version => version == selected ? verifiedEntry : version).ToArray() });
                            toolManagementView.VersionsGrid.SelectedItem = verifiedEntry;
                            toolManagementView.SetBusy(false, Status.Text);
                        }
                        return;
                    }
                    var message = action == "retire" ? ComponentImpact(selected) + $"\n\n移至可恢复区？\n{selected.SizeText}，此步骤仍占磁盘空间，可稍后恢复或永久删除。需要它的工程会提示缺少组件。"
                        : action == "restore" ? $"恢复 {selected.Id} / {selected.Version}？\n恢复前完整校验文件，不覆盖同一版本，不修改工程锁。"
                        : ComponentImpact(selected) + $"\n\n永久删除这个开发环境组件版本？\n此操作不可恢复。逻辑文件大小 {selected.SizeText}，硬链接会影响实际释放空间。\n需要此版本的工程和功能会提示缺少组件，需重新导入；源码、工程与内容锁保留。\n其他未登记工程的依赖无法核实，请先登记。";
                    if (MessageBox.Show(this, message, "开发环境组件版本管理", MessageBoxButton.YesNo,
                        action == "purge" ? MessageBoxImage.Warning : MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                    if (action == "retire") Log("开发环境组件已移至：" + await services.ToolManagement.RemoveAsync(selected, toolManagementProject, token));
                    else if (action == "restore") { await services.ToolManagement.RestoreAsync(selected, token); Log("开发环境组件版本已恢复：" + selected.Id + " / " + selected.Version); }
                    else if (action == "purge") { await services.ToolManagement.DeleteAsync(selected, toolManagementProject, token); Log("已永久删除所选开发环境组件：" + selected.Id + " / " + selected.Version); }
                }
                progressEnabled = false;
                await RefreshToolManagementAsync(token);
            }
            catch (Exception error)
            {
                progressEnabled = false;
                if (error is not OperationCanceledException)
                {
                    try { await RefreshToolManagementAsync(CancellationToken.None); }
                    catch (Exception refreshFailure) { Log(FailureDiagnostic(refreshFailure)); }
                    toolManagementView.SetBusy(false, "操作失败：" + error.Message);
                    toolManagementView.DetailText.Text = FailureDiagnostic(error);
                }
                throw;
            }
            finally { progressEnabled = false; toolManagementView.SetBusy(false); }
        });
    }
    private static string ComponentImpact(ManagedToolVersion selected) => $"{selected.Name}\n{selected.Id} / {selected.Version} · {selected.StateText}\n"
        + (selected.References.Count == 0 ? "检查范围内未发现依赖。" : $"受影响的依赖（{selected.References.Count} 项）：\n" + string.Join('\n', selected.References.Take(12))
            + (selected.References.Count > 12 ? "\n其余依赖请在管理页详情查看。" : ""));
    private async Task ShowToolManagementScopeAsync(CancellationToken token)
    {
        var registered = await services.ToolManagement.RegisteredAsync(token);
        var panel = new DockPanel { Margin = new(16) };
        var note = new TextBlock { Text = "自动检查当前与最近工程。以下列表可移除手动登记；最近工程请在欢迎页管理。离线或损坏的记录会阻止清理。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };
        DockPanel.SetDock(note, Dock.Top); panel.Children.Add(note);
        var list = new ListBox { ItemsSource = registered };
        var remove = new Button { Content = "移除所选手动登记", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 8, 0, 0) };
        DockPanel.SetDock(remove, Dock.Bottom); panel.Children.Add(remove);
        var all = new TextBox { Text = "本次检查的全部工程：\n" + string.Join('\n', toolManagementView!.Report?.Projects ?? []), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 8, 0, 0) };
        DockPanel.SetDock(all, Dock.Bottom); panel.Children.Add(all); panel.Children.Add(list);
        remove.Click += async (_, _) =>
        {
            if (list.SelectedItem is not string project) return;
            try { await services.ToolManagement.ForgetProjectAsync(project, token); list.ItemsSource = await services.ToolManagement.RegisteredAsync(token); }
            catch (Exception error) { Log(FailureDiagnostic(error)); MessageBox.Show(this, error.Message, "登记管理"); }
        };
        new Window { Owner = this, Title = "工程依赖检查范围", Width = 720, Height = 450, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)FindResource("Surface"), Foreground = (System.Windows.Media.Brush)FindResource("Text") }.ShowDialog();
        await RefreshToolManagementAsync(token);
    }
}
