namespace StudioX.Desktop;

using System.Windows;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    private async Task ExerciseAg32TimingControlsAsync(string directory, string fixture, Ag32PinPlanningView view,
        Action<bool, string> check, Func<Task> layout)
    {
        view.PlannerTabs.SelectedIndex = 2;
        check(view.ClockGuidanceText.Text.Contains("高于离线推荐范围", StringComparison.Ordinal) && view.RecommendedClocks.Items.Count == 2,
            "启用模拟 IP 后 200/100 显示频率建议，不自动降低频率");
        var assignments = view.GetAssignments();
        var analog = view.GetAnalog();
        var mainBefore = File.ReadAllBytes(PathBoundary.Resolve(fixture, "src/main.c"));
        view.SysClock.Text = "160";
        check(!view.SavePlanButton.IsEnabled && view.DisplayedTimingState == Ag32TimingState.Failed && view.ClockGuidanceText.Text.Contains("整数分频", StringComparison.Ordinal),
            "160/100 草稿实时标红并禁止保存");
        view.ApplyRecommendedClockButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await pendingOperation;
        check(!view.HasChanges && view.GetClocks() == new Ag32PinClockSettings(8, 160, 80), "推荐按钮经实际保存入口应用 160/80，保留 HSE");
        check(view.GetAssignments().SequenceEqual(assignments) && view.GetAnalog() == analog &&
            File.ReadAllBytes(PathBoundary.Resolve(fixture, "src/main.c")).SequenceEqual(mainBefore), "应用推荐保留引脚、模拟选择和用户主函数");
        check(File.ReadAllText(PathBoundary.Resolve(fixture, "device/studiox/StudioX_Board.h")).Contains("BOARD_PLL_FREQUENCY 160000000", StringComparison.Ordinal),
            "推荐时钟同步生成系统层频率头文件");
        check(view.DisplayedTimingState == Ag32TimingState.Unverified, "推荐保存完成仍为未验证，不能把推荐当成当前布线通过");
        view.HseClock.Text = "12";
        check(view.RecommendedClocks.Items.Count == 0 && !view.ApplyRecommendedClockButton.IsEnabled, "其它晶振不套用 8 MHz 验证记录");
        await RefreshAg32PinPlanAsync(fixture, CancellationToken.None, discardDraft: true);
        view.PlannerTabs.SelectedIndex = 2;
        await layout();
        view.ApplyRecommendedClockButton.BringIntoView();
        await layout();
        Render(this, Path.Combine(directory, "timing-recommendation.png"));
        var bounds = view.ApplyRecommendedClockButton.TransformToAncestor(Ag32PinMapping).TransformBounds(new Rect(view.ApplyRecommendedClockButton.RenderSize));
        check(bounds.Top >= 0 && bounds.Bottom <= Ag32PinMapping.ActualHeight, "推荐按钮可以完整滚动到可视区域");

        // 此处仅注入状态验证颜色与草稿行为，实际正/负余量由独立真实 Supra 回归覆盖。
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            foreach (var state in new[] { Ag32TimingState.Passed, Ag32TimingState.LowMargin, Ag32TimingState.Failed, Ag32TimingState.Stale })
            {
                var sample = new Ag32TimingStatus(state, "离线界面状态示例 · " + state,
                    new(8, 160, 80), state == Ag32TimingState.Failed ? -0.649m : 0.601m, 0.599m, 5780, 5780,
                    "rv32 → analog AHB bridge", "analog AHB bridge → rv32");
                view.SetTiming(sample);
                var color = state == Ag32TimingState.Failed ? "DiagnosticError" : state == Ag32TimingState.Passed ? "DiagnosticSuccess" : "DiagnosticWarning";
                check(view.DisplayedTimingState == state && view.TimingStateText.Foreground.ToString() == FindResource(color).ToString(),
                    theme.Id + ": timing state uses correct severity color " + state);
                check(view.TimingPathText.Text.Contains("AHB bridge", StringComparison.Ordinal), "关键路径可见");
                if (state == Ag32TimingState.Passed)
                {
                    view.TimingDetails.IsExpanded = true;
                    view.TimingStateText.BringIntoView();
                    await layout();
                    Render(this, Path.Combine(directory, "timing-colors-" + theme.Id + ".png"));
                    view.TimingDetails.IsExpanded = false;
                }
            }
            await layout();
        }
        ApplyTheme(ThemeService.Dark);
        var passed = new Ag32TimingStatus(Ag32TimingState.Passed, "UI fixture passed", new(8, 160, 80), 0.601m, 0.599m, 5780, 5780);
        view.SetTiming(passed, hasUnsavedText: true);
        check(view.DisplayedTimingState == Ag32TimingState.Unverified, "未保存 VE 文本不显示磁盘的绿色结果");
        view.SetTiming(passed);
        view.BusClock.Text = "40";
        check(view.DisplayedTimingState == Ag32TimingState.Unverified && !view.TimingReportButton.IsEnabled, "图形草稿改变立即清除绿色结果和旧报告入口");
        await RefreshAg32PinPlanAsync(fixture, CancellationToken.None, discardDraft: true);
        view.SetTiming(passed);
        await RefreshAg32PinPlanAsync(fixture, CancellationToken.None);
        check(view.DisplayedTimingState == Ag32TimingState.Unverified, "源文件未变时也刷新实际时序状态，清除注入旧状态");
    }
}
