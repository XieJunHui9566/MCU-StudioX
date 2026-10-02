namespace StudioX.Desktop;

using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    private string? ag32PinPlanProject;

    private void InitializeAg32PinPlanning()
    {
        Ag32PinMapping.Planner.ReloadRequested += ReloadAg32PinPlan_Click;
        Ag32PinMapping.Planner.SaveRequested += SaveAg32PinPlan_Click;
        Ag32PinMapping.Planner.OpenConstraintRequested += OpenAg32PinConstraint_Click;
        Ag32PinMapping.Planner.RecommendedClockRequested += ApplyAg32RecommendedClock_Click;
    }

    private void ClearAg32PinPlan()
    {
        ag32PinPlanProject = null;
        Ag32PinMapping.Planner.Clear();
    }

    private async Task RefreshAg32PinPlanAsync(string root, CancellationToken token, bool discardDraft = false)
    {
        var view = Ag32PinMapping.Planner;
        var timing = await services.Ag32PinPlanning.ReadTimingAsync(root, token);
        if (projectDirectory != root) return;
        var hasUnsavedText = currentProjectManifest?.PinMapping is { } mapping && FindEditor(mapping.PinMapFile)?.IsDirty == true;
        if (!discardDraft && ag32PinPlanProject == root && view.HasChanges)
        {
            // 窗口激活与工具状态刷新不能丢弃尚未保存的图形草稿。
            view.SetTiming(timing, hasUnsavedText);
            return;
        }
        var snapshot = await services.Ag32PinPlanning.ReadAsync(root, token);
        if (projectDirectory != root)
        {
            return;
        }
        if (!discardDraft && ag32PinPlanProject == root && view.Snapshot is { } previous &&
            previous.SourceSha256 == snapshot.SourceSha256 && previous.SourcePath == snapshot.SourcePath &&
            previous.DeviceId == snapshot.DeviceId && previous.TargetDevice == snapshot.TargetDevice)
        {
            view.SetTiming(timing, hasUnsavedText);
            return;
        }
        ag32PinPlanProject = root;
        view.SetSnapshot(snapshot);
        view.SetTiming(timing, hasUnsavedText);
        view.SetBusy(projectActionsBusy);
    }

    private async void ApplyAg32RecommendedClock_Click(object? sender, Ag32ClockRecommendation recommendation)
    {
        // RunAsync 会切换忙碌状态；先把用户点击的组合写入草稿，保存仍走原有冲突/散列检查。
        try { Ag32PinMapping.Planner.UseRecommendedClocks(recommendation); }
        catch (StudioXException error) { Log(error.ToString()); Status.Text = error.Message; return; }
        await RunAsync(async token => await SaveAg32PinPlanAsync(RequireProject(), token));
    }

    private async void ReloadAg32PinPlan_Click(object? sender, EventArgs e) => await RunAsync(async token =>
    {
        await RefreshAg32PinPlanAsync(RequireProject(), token, discardDraft: true);
        Status.Text = "已从磁盘重新读取 AG32 引脚与时钟配置。";
    });

    private async void SaveAg32PinPlan_Click(object? sender, EventArgs e) => await RunAsync(async token =>
    {
        await SaveAg32PinPlanAsync(RequireProject(), token);
    });

    private async Task SaveAg32PinPlanAsync(string root, CancellationToken token)
    {
        var view = Ag32PinMapping.Planner;
        var snapshot = view.Snapshot;
        if (snapshot is null || ag32PinPlanProject != root)
        {
            throw new StudioXException("AG32_PIN_PLAN_PROJECT", "图形配置不属于当前工程，请重新读取 .ve。");
        }
        if (FindEditor(snapshot.SourcePath)?.IsDirty == true)
        {
            throw new StudioXException("AG32_PIN_PLAN_DIRTY", "VE 编辑器有未保存内容；请先保存编辑器，并重新读取图形配置，避免覆盖文本修改。");
        }
        try
        {
            var result = await services.Ag32PinPlanning.ApplyAsync(root, snapshot,
                view.GetAssignments(), view.GetClocks(), view.GetAnalog(), token);
            if (projectDirectory != root)
            {
                return;
            }
            foreach (var relative in new[] { snapshot.SourcePath, Ag32SystemSupport.HeaderPath, Ag32SystemSupport.SourcePath, "device/studiox/StudioX_Board.h", CMakeGenerator.DeviceListPath })
            {
                if (FindEditor(relative) is { } editor)
                {
                    EditorSynchronizer.Apply(editor, await services.Files.ReadAsync(root, relative, token));
                }
            }
            RefreshProjectTree();
            view.ShowResult(result);
            try
            {
                await services.Intelligence.StartAsync(root, token);
                QueueLiveDiagnostics();
            }
            catch (Exception error) when (error is not OperationCanceledException) { Log("系统代码已生成，语言服务刷新失败：" + error); }
            await RefreshAg32PinMappingStatusAsync(token);
            Status.Text = "AG32 图形配置已保存并生成约束；顶部编译将重新生成映射镜像。";
            if (!string.IsNullOrWhiteSpace(result.ConverterDiagnostics))
            {
                Log("AG32 引脚约束转换：\n" + result.ConverterDiagnostics);
            }
        }
        catch (Exception error)
        {
            view.ShowFailure(error.Message);
            throw;
        }
    }

    private async void OpenAg32PinConstraint_Click(object? sender, string relative) => await RunAsync(async token =>
    {
        var root = RequireProject();
        if (ag32PinPlanProject != root || !(relative.StartsWith(".build/ag32-pin-plan/", StringComparison.Ordinal) || relative == ".build/ag32-mapping/logic_log.txt"))
        {
            throw new StudioXException("AG32_PIN_PLAN_PROJECT", "约束结果不属于当前引脚规划，请重新生成。");
        }
        await OpenSourceAsync(relative, token);
    });
}
