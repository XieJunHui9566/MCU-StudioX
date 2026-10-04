namespace StudioX.Desktop;

using System.Windows;

public partial class MainWindow
{
    private string? hdlWorkflowProject;
    private void InitializeHdlWorkflow()
    {
        HdlWorkflow.SaveBuildRequested += async (_, _) => await RunAsync(SaveHdlBuildSettingsAsync);
        HdlWorkflow.RunRequested += async (_, _) => await RunAsync(async token =>
        {
            var root = RequireProject();
            await SaveAllSourcesAsync(root, token);
            var settings = HdlWorkflow.ReadSimulation();
            await services.HdlWorkflow.SaveSimulationAsync(root, settings, token);
            HdlWorkflow.ClearResult("正在运行 RTL testbench，可使用顶部停止按钮取消…");
            try
            {
                var result = await services.HdlWorkflow.SimulateAsync(root, settings, token);
                if (projectDirectory == root)
                {
                    HdlWorkflow.ShowResult(result);
                }
                Log("RTL 仿真日志：" + result.LogPath);
                Status.Text = "RTL 仿真完成，波形已显示。";
            }
            catch (Exception error)
            {
                HdlWorkflow.ClearResult("仿真失败或取消，未显示旧结果。" + error.Message);
                throw;
            }
        });
        HdlWorkflow.EditBenchRequested += async (_, _) => await RunAsync(token => OpenSourceAsync(HdlWorkflow.TestbenchFile.Text.Trim(), token));
        HdlWorkflow.CreateBenchRequested += async (_, _) => await RunAsync(async token =>
        {
            var relative = HdlWorkflow.TestbenchFile.Text.Trim();
            await services.HdlWorkflow.CreateTestbenchAsync(RequireProject(), relative, HdlWorkflow.TestbenchTop.Text.Trim(), token);
            await OpenSourceAsync(relative, token);
        });
        HdlWorkflow.LogRequested += async (_, _) => await RunAsync(async token =>
        {
            if (HdlWorkflow.Result is { } result)
            {
                await OpenSourceAsync(Path.GetRelativePath(RequireProject(), result.LogPath).Replace('\\', '/'), token);
            }
        });
        HdlWorkflow.TimingReportRequested += async (_, _) => await RunAsync(async token =>
        {
            if (HdlWorkflow.TimingReport.SelectedItem is System.Windows.Controls.ComboBoxItem item)
            {
                await OpenSourceAsync(await services.HdlWorkflow.ReportPathAsync(RequireProject(), (string)item.Content, token), token);
            }
        });
        Activated += async (_, _) => await CheckHdlWorkflowFreshnessAsync();
        WorkspaceTabs.SelectionChanged += async (_, _) =>
        {
            if (HdlWorkflowTab.IsSelected)
            {
                await CheckHdlWorkflowFreshnessAsync();
            }
        };
    }

    private async Task CheckHdlWorkflowFreshnessAsync()
    {
        if (HdlWorkflow.Result is not { } result || projectActionsBusy || closing)
        {
            return;
        }
        try
        {
            var dirty = editorDocuments.Any(document => document.IsDirty && result.Inputs.ContainsKey(document.Source.RelativePath));
            if ((dirty || !await services.HdlWorkflow.IsCurrentAsync(result)) && ReferenceEquals(result, HdlWorkflow.Result))
            {
                HdlWorkflow.MarkStale();
            }
        }
        catch (Exception error) { Log("仿真快照检查失败：" + error); HdlWorkflow.MarkStale(); }
    }

    private async Task SaveHdlBuildSettingsAsync(CancellationToken token)
    {
        if (hdlWorkflowProject != projectDirectory || !HdlWorkflow.BuildDirty)
        {
            return;
        }
        await services.HdlWorkflow.SaveBuildAsync(RequireProject(), HdlWorkflow.ReadBuild(), token);
        HdlWorkflow.MarkBuildSaved();
        Status.Text = "逻辑构建配置已保存，下次编译生效。";
    }

    private async void OpenHdlWorkflow_Click(object? sender, EventArgs e) => await RunAsync(OpenHdlWorkflowAsync);
    public Task ShowHdlWorkflowPageAsync() => RunAsync(OpenHdlWorkflowAsync);
    private async Task OpenHdlWorkflowAsync(CancellationToken token)
    {
        var root = RequireProject();
        if (hdlWorkflowProject != root)
        {
            HdlWorkflow.Load(await services.HdlWorkflow.ReadBuildAsync(root, token), await services.HdlWorkflow.ReadSimulationAsync(root, token));
            HdlWorkflow.ClearResult("尚未运行 RTL 仿真，请提供 testbench。");
            hdlWorkflowProject = root;
        }
        HdlWorkflowTab.Visibility = Visibility.Visible;
        ShowDocument(HdlWorkflowTab);
    }
}
