namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StudioX.Application.Health;
using StudioX.Engine;

public partial class MainWindow
{
    private TabItem? projectHealthTab;
    private ProjectHealthView? projectHealthView;
    private string? healthDirectory;
    private int healthRequest;

    private void EnsureHealthView()
    {
        if (projectHealthTab is not null && WorkspaceTabs.Items.Contains(projectHealthTab)) return;
        projectHealthView = new ProjectHealthView
        {
            InspectRequested = deep => ShowProjectHealthAsync(deep), ChooseRequested = ChooseHealthProjectAsync,
            ExportRequested = ExportHealthReportAsync, ToolsRequested = () => ShowProjectToolsAsync(projectHealthView?.Report?.ProjectDirectory ?? projectDirectory),
            ActionRequested = RunHealthActionAsync, HelpRequested = id => ShowHelpAsync(id),
            CanAct = check => check.Action is HealthAction.None or HealthAction.Tools or HealthAction.ResetCache
                || projectHealthView?.Report?.ProjectDirectory == projectDirectory && projectDirectory is not null
        };
        projectHealthTab = AddToolTab("工程健康检查", projectHealthView);
    }
    private Task ShowProjectHealthAsync(bool deep = false, string? diagnostic = null) => RunAsync(async token =>
    {
        EnsureHealthView();
        ShowDocument(projectHealthTab!);
        var root = healthDirectory ?? projectDirectory;
        var request = ++healthRequest;
        projectHealthView!.BeginInspection();
        projectHealthView!.SetBusy(true, deep ? "正在完整校验开发环境组件，可用顶部停止按钮取消…" : "正在检查工程配置与必要工具入口…");
        try
        {
            var report = await services.ProjectHealth.InspectAsync(root, deep, new Progress<string>(text => { if (request == healthRequest) projectHealthView.SetBusy(true, text); }), token);
            ++healthRequest;
            if (!string.IsNullOrWhiteSpace(diagnostic))
            {
                var topic = services.Help.DiagnosticTopic(diagnostic);
                report = report with { Checks = report.Checks.Append(new HealthCheck("HEALTH_PREVIOUS_DIAGNOSTIC", "此次操作的原始诊断", HealthState.Information,
                    "此项保留进入检查前的失败日志，不用它推断当前配置仍然失败。", topic, RawDiagnostic: diagnostic)).ToArray() };
            }
            projectHealthView.SetReport(report);
            if (report.ProjectDirectory == projectDirectory)
            {
                guideHealthPassed = report.Errors == 0;
                RefreshFirstProjectGuide();
            }
            Status.Text = report.Summary;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            projectHealthView.SetBusy(false, "检查已取消；已完成的旧报告不代表本次检查结果。请重新检查。");
            throw;
        }
        finally { ++healthRequest; projectHealthView.SetBusy(false); }
    });
    private Task ChooseHealthProjectAsync()
    {
        var dialog = new OpenFolderDialog { Title = "选择要检查的 StudioX 工程目录" };
        if (dialog.ShowDialog(this) != true) return Task.CompletedTask;
        healthDirectory = dialog.FolderName;
        return ShowProjectHealthAsync();
    }
    private Task ExportHealthReportAsync() => RunAsync(async token =>
    {
        if (projectHealthView?.Report is not { } report) return;
        var dialog = new SaveFileDialog { Title = "保存到本机，不自动上传", Filter = "工程健康检查 JSON|*.json", FileName = "studiox-project-health.json" };
        if (dialog.ShowDialog(this) != true) return;
        await services.ProjectHealth.ExportAsync(report, dialog.FileName, token);
        Status.Text = "检查报告已保存：" + dialog.FileName;
    });
    private async Task RunHealthActionAsync(HealthCheck check)
    {
        if (check.Action == HealthAction.None) { await ShowHelpAsync(check.HelpTopic); return; }
        if (check.Action == HealthAction.Tools)
        {
            await ShowProjectToolsAsync(projectHealthView?.Report?.ProjectDirectory ?? projectDirectory);
            return;
        }
        var root = projectHealthView?.Report?.ProjectDirectory;
        if (root is null) return;
        if (check.Action == HealthAction.ResetCache)
        {
            await RunAsync(async token =>
            {
                EnsureNoActiveDebug();
                var plan = await services.ProjectHealth.PreviewCacheRepairAsync(root, token);
                if (plan.Entries.Count == 0) { Status.Text = "没有需要重建的配置缓存。"; return; }
                var text = "将以下生成内容移入工程 .build 内的备份目录：\n\n" + string.Join('\n', plan.Entries.Select(entry => entry.RelativePath))
                    + $"\n\n共 {plan.Bytes / 1048576d:F1} MiB。\n保留源码、SDK、sdkconfig、工程锁与固件文件。旧构建凭据一并备份，重新编译前不能下载。\n确认后，下次编译会重新配置。";
                if (MessageBox.Show(this, text, "预览配置缓存修复", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                var backup = await services.ProjectHealth.RepairCacheAsync(plan, token);
                BuildMemory.SetMessage("配置缓存已重建，重新编译后更新占用。");
                Log("配置缓存已移入备份：" + backup);
                var report = await services.ProjectHealth.InspectAsync(root, token: token);
                projectHealthView!.SetReport(report);
                Status.Text = "缓存已备份；下次编译重新配置。备份：" + backup;
            });
            return;
        }
        if (root != projectDirectory) { Status.Text = "请先在 IDE 打开这个工程，再进入编译或 SDK 设置。"; return; }
        if (check.Action == HealthAction.CMake) await RunAsync(token => OpenSourceAsync("CMakeLists.txt", token));
        else
        {
            await ShowProjectDetailsAsync();
            (check.Action == HealthAction.BuildSettings ? BuildSettingsCard : EspressifModulePanel).BringIntoView();
        }
    }
    private void PresentBuildHealth(ProjectHealthReport report)
    {
        EnsureHealthView();
        healthDirectory = report.ProjectDirectory;
        projectHealthView!.SetReport(report);
        ShowDocument(projectHealthTab!);
    }
}
